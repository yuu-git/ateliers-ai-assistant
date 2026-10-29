#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Ateliers.Ai.Assistant.Application.Logging;
using Ateliers.Ai.Assistant.Application.Models;
using Ateliers.Ai.Assistant.Application.Worker;
using Ateliers.Ai.Assistant.Desktop.ViewModels;
using Ateliers.Ai.Assistant.Infrastructure.Chat;
using Ateliers.Ai.Assistant.Infrastructure.Conversations;
using Ateliers.Ai.Assistant.Infrastructure.Storage;
using Ateliers.Ai.Assistant.Infrastructure.Worker;

namespace Ateliers.Ai.Assistant.Desktop.Diagnostics;

/// <summary>通常 AppData・Ollama・起動登録を触らず、UI 閉鎖後の Worker 生存とオフラインのログを確認する。</summary>
internal static class WorkerDisplayVerification
{
    /// <summary>接続あり・停止中の二段階を別ウィンドウで検証する。</summary>
    internal static MainWindow Create(string reportPath, string root, bool offline, bool notifications = false)
    {
        var paths = new AppDataPaths(Path.Combine(root, "r"), Path.Combine(root, "l"));
        var log = new FileDiagnosticLog(paths, "UI");
        log.Write(AppLogCategory.Application, AppLogLevel.Information, "verification.ui", "検証用 UI を開始しました。");
        var dummy = new DummyChatService(); var control = new EmptyControl();
        var session = new ChatModelSession(dummy, control, FileOperationGate.ForModels(paths));
        var settings = new SettingsViewModel(paths, new JsonUserSettingsStore(paths, log), log, _ => Task.CompletedTask);
        var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        var api = new WorkerApiClient(http, new WorkerConnectionFiles(paths));
        var chat = new ChatViewModel(session, session, action => action(), new SqliteConversationStore(paths, log), log);
        var web = new WebMonitoringViewModel(api, api, session, session, settings, log, action => action(), (_, _) => Task.CompletedTask);
        var worker = new WorkerViewModel(api, new Launcher(root), dummy, log) { RefreshResultsAsync = web.RefreshFeedAsync };
        var copied = "";
        var logs = new LogsViewModel(new FileAppLogReader(paths), text => { copied = text; return Task.CompletedTask; }, () => Task.CompletedTask);
        var window = new MainWindow(chat, new ModelManagementViewModel(session, action => action()), session, settings, web, worker, logs);
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1500, 1100));
        var report = new List<string>(); var started = false; var passed = false;
        window.Closed += (_, _) => { http.Dispose(); if (passed) report.Add("PASS: ALL WORKER UI CHECKS"); File.WriteAllLines(reportPath, report); };
        window.Activated += async (_, _) =>
        {
            if (started) return; started = true;
            try
            {
                await window.InitializedForVerification.WaitAsync(TimeSpan.FromSeconds(30));
                if (!offline)
                {
                    await worker.StartCommand.ExecuteAsync();
                    for (var attempt = 0; attempt < 50 && !worker.Connected; attempt++) { await Task.Delay(100); await worker.PollAsync(); }
                    if (!worker.Connected) throw new InvalidOperationException(worker.ErrorMessage);
                    worker.Model = "verification-model"; worker.AiPaused = true; worker.AllowLoad = true;
                    await worker.SaveOptionsCommand.ExecuteAsync();
                    window.ShowWebForVerification(); await Task.Delay(200); await window.ConfigureSourceForVerificationAsync(); await Task.Delay(100);
                    if (worker.IntervalMinutes != 5 || !worker.Automatic || worker.Url != "https://example.com/feed") throw new InvalidOperationException($"Native source input bindings failed: interval={worker.IntervalMinutes}, automatic={worker.Automatic}, url={worker.Url}, name={worker.Name}");
                    await worker.SaveSourceCommand.ExecuteAsync(); await worker.FetchCommand.ExecuteAsync();
                    for (var attempt = 0; attempt < 100 && worker.PendingCount != 10; attempt++) { await Task.Delay(100); await worker.PollAsync(); }
                    if (worker.PendingCount != 10 || !worker.Sources.Single().Detail.Contains("次回 ") || worker.Sources.Single().Detail.Contains("予定なし"))
                        throw new InvalidOperationException("Worker pending count or next run not displayed: " + worker.ErrorMessage);
                    report.Add("PASS: native source inputs, period edit, next schedule, paused AI and ten pending articles");
                    if (notifications) await NotificationFeatureVerification.RunAsync(window, web, worker, api, report);
                }
                else if (worker.Connected) throw new InvalidOperationException("Offline Worker incorrectly shown as connected");
                window.ShowLogsForVerification(); await Task.Delay(200); await logs.RefreshAsync();
                if (logs.Entries.Count == 0 || window.LogsForVerification.Count != logs.Entries.Count || !logs.Entries.Any(row => row.Entry.Origin == "Worker"))
                    throw new InvalidOperationException("Persisted logs unavailable without Worker");
                window.SelectLogsForVerification(); await logs.CopySelectedCommand.ExecuteAsync();
                if (!copied.Contains("操作:") || window.LogsForVerification.Detail.Length == 0) throw new InvalidOperationException("Native log selection/detail/copy failed");
                report.Add("PASS: native application logs, UI/Worker merge, multi-selection, details and explicit plain-text copy");
                chat.SelectedModel = "dummy"; await chat.ModelChangeTask; chat.InputText = "Worker と独立したチャット";
                await chat.SendCommand.ExecuteAsync();
                if (chat.Messages.Count != 2) throw new InvalidOperationException("Chat unavailable without Worker");
                report.Add(offline ? "PASS: Worker offline, saved logs and independent chat remain usable" : "PASS: UI close is independent from Worker lifecycle");
                chat.ConfirmLeaveAsync = () => Task.FromResult(ConversationLeaveDecision.Discard);
                passed = true;
                RequestClose(window);
            }
            catch (Exception exception) { report.Add("FAIL: " + exception); File.WriteAllLines(reportPath, report); chat.ConfirmLeaveAsync = () => Task.FromResult(ConversationLeaveDecision.Discard); RequestClose(window); }
        };
        return window;
    }
    private static void RequestClose(MainWindow window) => PostMessage(WinRT.Interop.WindowNative.GetWindowHandle(window), 0x0112, (nuint)0xF060, 0);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
    private sealed class Launcher(string root) : IWorkerLauncher
    {
        public Task StartAsync()
        {
            var executable = Path.Combine(AppContext.BaseDirectory, "Worker", "Ateliers.Ai.Assistant.Worker.exe");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
            start.ArgumentList.Add("--verification-root"); start.ArgumentList.Add(root);
            var process = Process.Start(start)!; File.WriteAllText(Path.Combine(root, "worker.pid"), process.Id.ToString());
            return Task.CompletedTask;
        }
        public Task<WorkerStartupState> GetStartupAsync() => Task.FromResult(new WorkerStartupState(false, "検証では Windows の起動登録を変更しません。"));
        public Task<WorkerStartupState> SetStartupAsync(bool enabled) => Task.FromResult(new WorkerStartupState(enabled, "検証用の登録状態"));
    }
    private sealed class EmptyControl : IModelControlService
    {
        public Task<IReadOnlyList<LoadedModel>> GetLoadedModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LoadedModel>>([]);
        public Task LoadModelAsync(string model, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UnloadModelAsync(string model, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
#endif
