#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ateliers.Ai.Assistant.Application.Models;
using Ateliers.Ai.Assistant.Desktop.Images;
using Ateliers.Ai.Assistant.Desktop.ViewModels;
using Ateliers.Ai.Assistant.Infrastructure.Chat;
using Ateliers.Ai.Assistant.Infrastructure.Conversations;
using Ateliers.Ai.Assistant.Infrastructure.Storage;
using Microsoft.UI.Dispatching;
using System.Runtime.InteropServices;
using System.Net.Http;

namespace Ateliers.Ai.Assistant.Desktop.Diagnostics;

/// <summary>利用者の AppData を使わず、実ウィンドウの終了保存と次プロセスの復元を検証する。</summary>
internal static class StorageVerification
{
    /// <summary>実際の確認ダイアログを UI Automation で操作するための隔離ウィンドウを作る。</summary>
    internal static MainWindow CreateInteractive(string root, bool liveOllama = false)
    {
        var paths = new AppDataPaths(Path.Combine(root, "Roaming"), Path.Combine(root, "Local"));
        var log = new FileDiagnosticLog(paths);
        var client = liveOllama ? new HttpClient { BaseAddress = new Uri("http://localhost:11434/"), Timeout = Timeout.InfiniteTimeSpan } : null;
        var adapter = client is null ? null : new OllamaChatService(client);
        var session = adapter is null ? new ChatModelSession(new DummyChatService(), new EmptyControl()) : new ChatModelSession(adapter, adapter);
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        void Dispatch(Action action) { if (dispatcher.HasThreadAccess) action(); else dispatcher.TryEnqueue(() => action()); }
        var vm = new ChatViewModel(session, session, Dispatch, new SqliteConversationStore(paths, log), log);
        var settings = new SettingsViewModel(paths, new JsonUserSettingsStore(paths, log), log, _ => Task.CompletedTask);
        var window = new MainWindow(vm, new ModelManagementViewModel(session, Dispatch), session, settings);
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1300, 1000));
        window.Closed += (_, _) => client?.Dispose();
        return window;
    }

    /// <summary>同じ一時保存先で create / resume の二段階検証を行う。</summary>
    public static MainWindow Create(string reportPath, string root, bool resume)
    {
        var paths = new AppDataPaths(Path.Combine(root, "Roaming"), Path.Combine(root, "Local"));
        var log = new FileDiagnosticLog(paths);
        var store = new SqliteConversationStore(paths, log);
        var session = new ChatModelSession(new DummyChatService(), new EmptyControl());
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        void Dispatch(Action action) { if (dispatcher.HasThreadAccess) action(); else dispatcher.TryEnqueue(() => action()); }
        var vm = new ChatViewModel(session, session, Dispatch, store, log);
        var settings = new SettingsViewModel(paths, new JsonUserSettingsStore(paths, log), log, _ => Task.CompletedTask);
        var window = new MainWindow(vm, new ModelManagementViewModel(session, Dispatch), session, settings);
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1300, 1000));
        var report = new List<string>();
        var started = false; var passed = false; var closed = false;
        window.Closed += (_, _) =>
        {
            closed = true;
            if (passed) { report.Add("PASS: native window close completed after pending work"); report.Add("PASS: ALL STORAGE DISPLAY CHECKS"); }
            File.WriteAllLines(reportPath, report);
        };
        window.Activated += async (_, _) =>
        {
            if (started) return; started = true;
            try
            {
                await window.InitializedForVerification.WaitAsync(TimeSpan.FromSeconds(20));
                if (!await window.TranscriptForVerification.Ready.WaitAsync(TimeSpan.FromSeconds(20))) throw new InvalidOperationException("WebView2 unavailable");
                if (!resume)
                {
                    if (File.Exists(paths.DatabaseFile)) throw new InvalidOperationException("Initial DB must not exist");
                    vm.SelectedModel = "dummy"; await vm.ModelChangeTask;
                    using var source = await ImagePasteVerification.CreateRedImageAsync();
                    var image = await ClipboardImageService.NormalizeAsync(source, CancellationToken.None);
                    vm.InputText = "# 保存する原文\n\n**画像付き会話**";
                    vm.TryBeginImagePaste(); vm.CompleteImagePaste(image);
                    await vm.SendCommand.ExecuteAsync();
                    vm.InputText = "未送信の入力も復元する"; vm.TryBeginImagePaste(); vm.CompleteImagePaste(image);
                    await WaitDom(window, "document.querySelectorAll('.attachment:not([hidden])').length === 1 && document.querySelector('.attachment:not([hidden])').naturalWidth === 1536");
                    vm.ConfirmLeaveAsync = () => Task.FromResult(ConversationLeaveDecision.Cancel);
                    RequestClose(window); await Task.Delay(200);
                    if (closed || vm.InputText.Length == 0 || File.Exists(paths.DatabaseFile))
                        throw new InvalidOperationException($"Canceled close lost memory: closed={closed}; draftLength={vm.InputText.Length}; dbExists={File.Exists(paths.DatabaseFile)}");
                    report.Add("PASS: Markdown/image history, unsaved draft, and native close cancellation");
                    vm.ConfirmLeaveAsync = () => Task.FromResult(ConversationLeaveDecision.Save);
                    vm.RequestConversationTitleAsync = async _ => { await Task.Delay(400); return "実画面の保存検証"; };
                    RequestClose(window); await Task.Delay(100);
                    if (closed) throw new InvalidOperationException("Window closed before asynchronous save confirmation");
                    report.Add("PASS: window stays alive while awaiting save confirmation and SQLite write");
                    passed = true;
                }
                else
                {
                    if (vm.SelectedModel != "dummy" || vm.SavedConversations.Count != 1) throw new InvalidOperationException("Settings/list not restored");
                    vm.SelectedSavedConversation = vm.SavedConversations.Single();
                    await vm.OpenConversationCommand.ExecuteAsync();
                    if (vm.Messages.Count != 2 || vm.InputText != "未送信の入力も復元する" || vm.PendingImage is null || vm.Messages[0].Image is null)
                        throw new InvalidOperationException("Persisted images/draft did not survive process restart");
                    await WaitDom(window, "document.querySelectorAll('.attachment:not([hidden])').length === 1 && document.querySelector('.body').innerText.includes('画像付き会話')");
                    await vm.SendCommand.ExecuteAsync();
                    var restored = (await store.LoadAsync(vm.SavedConversations.Single().Id))!;
                    if (restored.Messages.Count != 4 || restored.DraftImage is not null || vm.HasUnsavedConversationChanges) throw new InvalidOperationException("Follow-up auto-save failed");
                    report.Add("PASS: new process restores settings, raw Markdown, native preview, image DOM, draft, and auto-saved follow-up");
                    vm.ConfirmDeleteConversationAsync = _ => Task.FromResult(false);
                    await vm.DeleteConversationCommand.ExecuteAsync();
                    if (vm.SavedConversations.Count != 1) throw new InvalidOperationException("Canceled deletion changed data");
                    vm.ConfirmDeleteConversationAsync = _ => Task.FromResult(true);
                    await vm.DeleteConversationCommand.ExecuteAsync();
                    if (vm.SavedConversations.Count != 0 || vm.Messages.Count != 0 || vm.PendingImage is not null) throw new InvalidOperationException("Delete failed");
                    report.Add("PASS: deletion cancellation and cascade deletion clear native/history views");
                    vm.InputText = "保存せず終了";
                    vm.ConfirmLeaveAsync = () => Task.FromResult(ConversationLeaveDecision.Discard);
                    passed = true; RequestClose(window);
                }
            }
            catch (Exception exception)
            {
                report.Add("FAIL: " + exception); passed = false;
                File.WriteAllLines(reportPath, report);
                vm.ConfirmLeaveAsync = () => Task.FromResult(ConversationLeaveDecision.Discard);
                RequestClose(window);
            }
        };
        return window;
    }

    /// <summary>Window.Close による直接破棄ではなく、利用者の閉じる操作を再現する。</summary>
    private static void RequestClose(MainWindow window) => PostMessage(WinRT.Interop.WindowNative.GetWindowHandle(window), 0x0112, (nuint)0xF060, 0);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

    /// <summary>最新の描画が実 DOM へ反映されるまで待つ。</summary>
    private static async Task WaitDom(MainWindow window, string expression)
    {
        for (var attempt = 0; attempt < 150; attempt++)
        { if (await window.TranscriptForVerification.CoreForVerification.ExecuteScriptAsync(expression) == "true") return; await Task.Delay(50); }
        throw new InvalidOperationException("Storage image/Markdown DOM check timed out");
    }

    /// <summary>Ollama を起動せずモデル選択・終了処理を接続する検証用アダプター。</summary>
    private sealed class EmptyControl : IModelControlService
    {
        /// <inheritdoc/>
        public Task<IReadOnlyList<LoadedModel>> GetLoadedModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LoadedModel>>(Array.Empty<LoadedModel>());
        /// <inheritdoc/>
        public Task LoadModelAsync(string model, CancellationToken cancellationToken = default) => Task.CompletedTask;
        /// <inheritdoc/>
        public Task UnloadModelAsync(string model, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
#endif
