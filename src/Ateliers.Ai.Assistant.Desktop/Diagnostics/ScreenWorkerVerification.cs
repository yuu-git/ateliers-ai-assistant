#if DEBUG
using System;
using System.Linq;
using System.IO;
using System.Net.Http;


using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Ateliers.Ai.Assistant.Infrastructure.Storage;
using Ateliers.Ai.Assistant.Infrastructure.Worker;
using Ateliers.Ai.Assistant.Infrastructure.Chat;
using Ateliers.Ai.Assistant.Infrastructure.Conversations;
using Ateliers.Ai.Assistant.Application.Models;
using Ateliers.Ai.Assistant.Desktop.ViewModels;


namespace Ateliers.Ai.Assistant.Desktop.Diagnostics;

/// <summary>実際の Desktop プロセスで独立 Worker への再接続を検証する。</summary>
internal static class ScreenWorkerVerification
{
    /// <summary>製品の監視 ViewModel と Markdown 表示で再接続を確認する。</summary>
    public static Window CreateMonitor(string root, string report)
    {
        var paths = new AppDataPaths(Path.Combine(root, "r"), Path.Combine(root, "l"));
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        void Dispatch(Action action) { if (dispatcher.HasThreadAccess) action(); else dispatcher.TryEnqueue(() => action()); }
        var diagnostics = new FileDiagnosticLog(paths, "UI");
        var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
        var api = new WorkerApiClient(http, new(paths));
        var chatHttp = new HttpClient { BaseAddress = new Uri("http://localhost:11434/") };
        var ollama = new OllamaChatService(chatHttp);
        var session = new ChatModelSession(ollama, ollama, FileOperationGate.ForModels(new AppDataPaths())) { CanAutomaticallyUnloadAsync = api.CanAutomaticallyUnloadAsync };
        var settings = new SettingsViewModel(paths, new JsonUserSettingsStore(paths, diagnostics), diagnostics, _ => Task.CompletedTask);
        var chat = new ChatViewModel(session, session, Dispatch, new SqliteConversationStore(paths, diagnostics), diagnostics);
        var worker = new WorkerViewModel(api, new Storage.WorkerLauncher(paths), session, diagnostics);
        var web = new WebMonitoringViewModel(api, api, session, session, settings, diagnostics, Dispatch, (_, _) => Task.CompletedTask);
        var window = new MainWindow(chat, new ModelManagementViewModel(session, Dispatch), session, settings, web, worker);
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1500, 1100));
        var vm = new ScreenMonitorViewModel(api, session); window.InitializeScreenMonitor(vm);
        var view = window.MonitorForVerification;
        var started = false; var result = "FAIL: 検証が完了しませんでした。";
        window.Activated += async (_, _) =>
        {
            if (started) return; started = true;
            try
            {
                await window.InitializedForVerification.WaitAsync(TimeSpan.FromSeconds(20));
                window.NavigateForVerification("Monitor");
                await vm.RefreshCommand.ExecuteAsync();
                if (!await view.TranscriptForVerification.Ready.WaitAsync(TimeSpan.FromSeconds(15))) throw new InvalidOperationException("Markdown 表示初期化失敗");
                await Task.Delay(1200);
                var rendered = await view.TranscriptForVerification.CoreForVerification.ExecuteScriptAsync("Boolean(document.querySelector('#transcript .body')?.textContent.trim() && /[0-9]{2}:[0-9]{2}:[0-9]{2}/.test(document.querySelector('#transcript .detail')?.textContent || ''))");
                if (rendered != "true") throw new InvalidOperationException("時刻付きコメントの DOM 反映失敗");
                var list = await api.ListMonitorsAsync();
                foreach (var monitor in vm.Monitors.ToArray())
                {
                    vm.Selected = monitor; await vm.PollAsync(); await Task.Delay(150);
                    if (list.Monitors.First(m => m.Definition.MonitorId == monitor.Id).Captures > 0 && vm.Comments.Count == 0) throw new InvalidOperationException("監視別のコメント復元失敗");
                }
                var status = list.Monitors.First(m => m.Active);
                if (!status.Active || vm.Comments.Count == 0 || vm.Comments[^1].Content.Length == 0) throw new InvalidOperationException("監視とコメントの復元失敗");
                // Desktop の管理対象にも入れ、通常 MainWindow 終了時の自動解放保護を通す。
                await session.LoadAsync(status.Definition.Settings.Model!);
                result = System.Text.Json.JsonSerializer.Serialize(list, WorkerJsonContext.Default.ScreenMonitorList);
            }
            catch (Exception exception) { result = "FAIL: " + exception; }
            finally { PostMessage(WinRT.Interop.WindowNative.GetWindowHandle(window), 0x10, 0, 0); }
        };
        window.Closed += (_, _) => { File.WriteAllText(report, session.IsClosing ? result : "FAIL: 通常の非同期終了処理を通りませんでした。"); http.Dispose(); chatHttp.Dispose(); };
        return window;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
}
#endif
