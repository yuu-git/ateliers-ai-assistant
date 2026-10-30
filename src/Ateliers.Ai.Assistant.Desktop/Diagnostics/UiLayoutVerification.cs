#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Ateliers.Ai.Assistant.Application.Models;
using Ateliers.Ai.Assistant.Application.Notifications;
using Ateliers.Ai.Assistant.Application.WebMonitoring;
using Ateliers.Ai.Assistant.Application.Worker;
using Ateliers.Ai.Assistant.Desktop.ViewModels;
using Ateliers.Ai.Assistant.Infrastructure.Chat;
using Ateliers.Ai.Assistant.Infrastructure.Storage;

namespace Ateliers.Ai.Assistant.Desktop.Diagnostics;

/// <summary>隔離データ・偽 Worker で実 XAML、Markdown の位置保持、幅変更、設定編集を確認する。</summary>
internal static class UiLayoutVerification
{
    /// <summary>スクリーンショットとの同期を伴う検証ウィンドウを作成する。</summary>
    internal static MainWindow Create(string reportPath, string root)
    {
        var paths = new AppDataPaths(Path.Combine(root, "r"), Path.Combine(root, "l"));
        var log = new FileDiagnosticLog(paths, "UI"); var api = new PreviewClient();
        var chat = new DummyChatService(); var session = new ChatModelSession(chat, new EmptyModels());
        var opened = 0;
        var settings = new SettingsViewModel(paths, new JsonUserSettingsStore(paths, log), log, _ => { opened++; return Task.CompletedTask; });
        var web = new WebMonitoringViewModel(api, api, session, session, settings, log, action => action(), (_, _) => Task.CompletedTask);
        var worker = new WorkerViewModel(api, api, chat, log) { RefreshResultsAsync = web.RefreshFeedAsync };
        var logs = new LogsViewModel(new FileAppLogReader(paths), _ => Task.CompletedTask, () => Task.CompletedTask);
        var window = new MainWindow(new ChatViewModel(session, session, action => action()), new ModelManagementViewModel(session, action => action()), session, settings, web, worker, logs);
        var report = new List<string>(); var started = false;
        window.Closed += (_, _) => File.WriteAllLines(reportPath, report);
        window.Activated += async (_, _) =>
        {
            if (started) return; started = true;
            try
            {
                await window.InitializedForVerification.WaitAsync(TimeSpan.FromSeconds(30));
                report.Add($"INFO: native rasterization scale {((Microsoft.UI.Xaml.FrameworkElement)window.Content).XamlRoot.RasterizationScale}");
                window.AppWindow.Move(new(30, 30)); window.AppWindow.Resize(new(1600, 1040));
                window.ShowWebForVerification(); await Task.Delay(500);
                var view = window.WebForVerification;
                view.SelectSourceForVerification(1); view.SelectForVerification(0);
                var preview = view.PreviewForVerification;
                if (!await preview.Ready.WaitAsync(TimeSpan.FromSeconds(20))) throw new InvalidOperationException("WebView unavailable");
                await WaitDom(preview, "document.querySelector('h1')?.textContent === 'UI 改修の検証' && document.querySelector('code[data-highlighted]') !== null");
                await preview.CoreForVerification.ExecuteScriptAsync("window.scrollTo(0,600)"); await Task.Delay(150);
                var scroll = await preview.CoreForVerification.ExecuteScriptAsync("window.scrollY");
                if (double.Parse(scroll, System.Globalization.CultureInfo.InvariantCulture) < 100) throw new InvalidOperationException("Long Markdown did not scroll");
                var selected = web.SelectedNotification; var source = web.SelectedFeedSource;
                await worker.PollAsync(); await web.RefreshFeedAsync(); await Task.Delay(250);
                if (!ReferenceEquals(selected, web.SelectedNotification) || !ReferenceEquals(source, web.SelectedFeedSource) ||
                    await preview.CoreForVerification.ExecuteScriptAsync("window.scrollY") != scroll) throw new InvalidOperationException("Polling reset selection or Markdown scroll");
                await preview.CoreForVerification.ExecuteScriptAsync("window.scrollTo(0,0)");
                await Capture(root, "notifications-wide");
                view.SelectViewForVerification(1); await Task.Delay(100);
                if (!view.ViewSelectionForVerification(1) || !ReferenceEquals(source, web.SelectedFeedSource)) throw new InvalidOperationException("Wide view navigation did not preserve source or sync selection");
                view.SelectViewForVerification(0); await Task.Delay(100);
                view.SelectForVerification(0);
                await view.CaptureManagerForVerificationAsync(() => Capture(root, "source-management"));
                if (!view.LayoutForVerification.Sources || view.LayoutForVerification.List < 250 || view.LayoutForVerification.Detail < 300) throw new InvalidOperationException("Wide layout invalid");
                report.Add("PASS: native feed selection, article URL separation, safe Markdown, C# highlighting and polling scroll preservation");
                view.HistoryForVerification(true); await Capture(root, "history-wide"); view.HistoryForVerification(false);
                view.SelectForVerification(0); await Task.Delay(200);
                window.AppWindow.Resize(new(1150, 950)); await Task.Delay(300); await Capture(root, "notifications-medium");
                if (view.LayoutForVerification.Sources || view.LayoutForVerification.Back) throw new InvalidOperationException("Medium layout invalid");
                window.AppWindow.Resize(new(760, 950)); await Task.Delay(300); await Capture(root, "notifications-narrow-detail");
                if (!view.LayoutForVerification.Back) throw new InvalidOperationException("Narrow back button missing");
                view.BackForVerification(); await Task.Delay(150); await Capture(root, "notifications-narrow-list");
                if (web.SelectedNotification is not null) throw new InvalidOperationException("Back did not end article selection");
                window.AppWindow.Resize(new(480, 800)); await Task.Delay(300); await Capture(root, "notifications-small");
                view.SelectViewForVerification(2); await Task.Delay(100);
                if (!view.ViewSelectionForVerification(2) || !ReferenceEquals(source, web.SelectedFeedSource)) throw new InvalidOperationException("Compact view navigation did not preserve source or sync selection");
                view.SelectViewForVerification(0); await Task.Delay(100);
                window.AppWindow.Resize(new(1600, 1040)); await Task.Delay(250);
                view.SelectForVerification(0); await WaitDom(preview, "document.querySelector('h1')?.textContent === 'UI 改修の検証'");
                if (await preview.CoreForVerification.ExecuteScriptAsync("window.scrollY") != "0") throw new InvalidOperationException("Resize changed Markdown position");
                report.Add("PASS: wide/medium/narrow layouts, history toggle, list/detail back and article preservation");
                window.NavigateForVerification("Settings"); await Task.Delay(300);
                window.WorkerOptionsForVerification.EditModelForVerification("未保存のモデル入力"); await Task.Delay(100);
                if (worker.Model != "未保存のモデル入力") throw new InvalidOperationException("Native AI input binding failed");
                await worker.PollAsync(); if (!worker.HasAiEdits || worker.Model != "未保存のモデル入力") throw new InvalidOperationException("Poll replaced AI input");
                await Capture(root, "settings-wide");
                window.AppWindow.Resize(new(760, 950)); await Task.Delay(250); await Capture(root, "settings-narrow");
                window.ScrollSettingsForVerification(true); await Capture(root, "settings-data"); window.ScrollSettingsForVerification(false);
                window.AppWindow.Resize(new(480, 800)); await Task.Delay(250); await Capture(root, "settings-small");
                await settings.OpenSettingsCommand.ExecuteAsync(); await settings.OpenLocalCommand.ExecuteAsync();
                await settings.OpenDatabaseCommand.ExecuteAsync(); await settings.OpenLogsCommand.ExecuteAsync();
                if (opened != 4) throw new InvalidOperationException("Fixed folder commands missing");
                report.Add("PASS: native settings edit, unsaved indication, three categories, narrow form and four fixed-folder commands");
                foreach (var tag in new[] { "Chat", "Models", "Logs", "Settings", "Web" }) { window.NavigateForVerification(tag); await Task.Delay(100); }
                view.SelectForVerification(0);
                var deletion = web.DeleteArticlesCommand.ExecuteAsync();
                await Capture(root, "bulk-delete"); await deletion;
                if (web.Notifications.Count != 7 || web.VisibleNotifications.Count != 3 || !ReferenceEquals(selected, web.SelectedNotification))
                    throw new InvalidOperationException("Native filtered deletion lost scope or remaining selection");
                report.Add("PASS: native bulk-delete dialog shows only filtered entries, confirms selected ID and preserves another selected article");
                api.Offline = true; await worker.PollAsync(); await Capture(root, "worker-offline");
                if (worker.Connected || !ReferenceEquals(source, web.SelectedFeedSource) || !ReferenceEquals(selected, web.SelectedNotification)) throw new InvalidOperationException("Offline refresh discarded browsing state");
                report.Add("PASS: all existing navigation IDs, offline status and preserved cached reading state");
                api.Offline = false; await worker.PollAsync(); await web.RefreshFeedAsync();
                await view.InvokeArticleForVerificationAsync(0, bookmark: false, delete: true);
                await Capture(root, "single-delete-cancel"); await WaitForIdle(web);
                if (web.Notifications.Count != 7 || !settings.ConfirmSingleArticleDeletion || !ReferenceEquals(selected, web.SelectedNotification)) throw new InvalidOperationException("Cancel changed deletion or preference state");
                await view.InvokeArticleForVerificationAsync(0, bookmark: false, delete: true);
                await Capture(root, "single-delete-confirm"); await WaitForIdle(web);
                if (web.Notifications.Count != 6 || settings.ConfirmSingleArticleDeletion || web.SelectedDocument is not null) throw new InvalidOperationException("Single confirmation did not delete selected row or save preference");
                await view.InvokeArticleForVerificationAsync(0, bookmark: false, delete: true); await WaitForIdle(web);
                if (web.Notifications.Count != 5) throw new InvalidOperationException("Suppressed native delete did not complete");
                window.NavigateForVerification("Settings"); window.ScrollSettingsForVerification(true);
                await Capture(root, "single-delete-settings"); await settings.FlushAsync();
                if (!settings.ConfirmSingleArticleDeletion || !(await new JsonUserSettingsStore(paths, log).LoadAsync()).ConfirmSingleArticleDeletion) throw new InvalidOperationException("Native settings checkbox did not re-enable confirmation");
                report.Add("PASS: native single delete cancel/confirm/skip, selected detail clear and settings checkbox re-enable");
                report.Add("PASS: ALL UI REFACTORING CHECKS");
            }
            catch (Exception exception) { report.Add("FAIL: " + exception); }
            finally { var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window); PostMessage(hwnd, 0x0112, (IntPtr)0xF060, IntPtr.Zero); }
        };
        return window;
    }
    private static async Task WaitForIdle(WebMonitoringViewModel web)
    { for (var i = 0; i < 100; i++) { if (!web.IsRunning) return; await Task.Delay(50); } throw new TimeoutException("Single deletion did not complete"); }
    private static async Task WaitDom(Controls.ChatTranscriptView preview, string expression)
    {
        for (var i = 0; i < 100; i++) { if (await preview.CoreForVerification.ExecuteScriptAsync(expression) == "true") return; await Task.Delay(50); }
        throw new InvalidOperationException("Markdown DOM not ready");
    }
    private static async Task Capture(string root, string name)
    {
        await Task.Delay(300); await File.WriteAllTextAsync(Path.Combine(root, "capture-request.txt"), name);
        for (var i = 0; i < 150; i++) { if (File.Exists(Path.Combine(root, name + ".captured"))) return; await Task.Delay(100); }
        throw new TimeoutException("Screenshot not acknowledged: " + name);
    }
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    private sealed class EmptyModels : IModelControlService
    {
        public Task<IReadOnlyList<LoadedModel>> GetLoadedModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LoadedModel>>([]);
        public Task LoadModelAsync(string model, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UnloadModelAsync(string model, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class PreviewClient : IWorkerClient, IWorkerLauncher, IWebMonitoringReader, IWebCollectionOperations
    {
        private WorkerConfiguration _configuration = WorkerConfiguration.Default with { SummaryModel = "dummy", AiPaused = true, Sources = [new(Guid.NewGuid(), "開発ツールの更新", "https://example.com/releases.atom", 60, true), new(Guid.NewGuid(), "技術記事", "https://example.com/blog.xml", 120, false)] };
        private IReadOnlyList<ArticleNotification> _notifications;
        public bool Offline;
        public PreviewClient()
        {
            var body = "# UI 改修の検証\n\n**これは検証用データです。**\n\n- 通知の閲覧\n- 情報源ごとの表示\n\n| 項目 | 状態 |\n| --- | --- |\n| 設定 | 保存済み |\n\n```csharp\nvar total = Enumerable.Range(1, 10).Sum();\nConsole.WriteLine(total);\n```\n\n" + string.Join("\n\n", Enumerable.Range(1, 150).Select(index => $"段落 {index}: 長文記事のスクロール位置が定期更新で失われないことを確認します。"));
            _notifications = Enumerable.Range(0, 8).Select(index => new ArticleNotification(new(Guid.NewGuid(), DateTimeOffset.Now.AddMinutes(-index), NotificationKind.Information,
                "検証用の更新情報 " + (index + 1), body), "https://example.com/articles/" + index, "verification.md", DateTimeOffset.Now.AddHours(-index), "検証用モデル", _configuration.Sources[index % 2].Url,
                FetchedAt: DateTimeOffset.Now.AddHours(-index), IsBookmarked: index == 2, Tags: index == 2 ? [new ArticleTag(Guid.NewGuid(), "確認用")] : [])).ToArray();
        }
        public Task<WorkerStatus> GetStatusAsync(CancellationToken token = default)
        {
            if (Offline) throw new WorkerException("検証用: Worker に接続できません。設定から開始してください。", connectionFailure: true);
            return Task.FromResult(new WorkerStatus(1, WorkerCoordinator.ImplementationVersion, Guid.Empty, DateTimeOffset.Now.AddHours(-1), _configuration,
                _configuration.Sources.Select(source => new SourceState(source.Id, DateTimeOffset.Now.AddMinutes(-10), DateTimeOffset.Now.AddMinutes(50), "取得完了（要約は別処理）")).ToArray(), 3, "一時停止中（RSS 取得は継続）", "", false, false));
        }
        public Task<WorkerConfiguration> SaveConfigurationAsync(WorkerConfiguration configuration, CancellationToken token = default) { _configuration = configuration with { Version = configuration.Version + 1 }; return Task.FromResult(_configuration); }
        public Task<FetchAccepted> FetchAsync(FetchRequest request, CancellationToken token = default) => Task.FromResult(new FetchAccepted(request.RequestId, false));
        public Task<FetchAccepted> StopAsync(CancellationToken token = default) => Task.FromResult(new FetchAccepted(Guid.Empty, false));
        public Task<IReadOnlyList<CollectionRun>> GetRunsAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<CollectionRun>>([new(Guid.NewGuid(), DateTimeOffset.Now.AddMinutes(-10), DateTimeOffset.Now.AddMinutes(-9), "取得完了（要約は別処理）", 10, 2, 8, Source: _configuration.Sources[0].Url)]);
        public Task<IReadOnlyList<ArticleNotification>> GetNotificationsAsync(CancellationToken token = default) => Task.FromResult(_notifications);
        public Task<ArticleDeletionResult> DeleteArticlesAsync(IReadOnlyList<Guid> ids, CancellationToken token = default)
        { _notifications = _notifications.Where(item => !ids.Contains(item.Notification.Id)).ToArray(); return Task.FromResult(new ArticleDeletionResult(ids, [], false, "")); }
        public Task<IReadOnlyList<CollectionRun>> RunsAsync(CancellationToken token) => GetRunsAsync(token);
        public Task<IReadOnlyList<ArticleNotification>> NotificationsAsync(CancellationToken token) => GetNotificationsAsync(token);
        public Task<ArticleDeletionResult> DeleteAsync(IReadOnlyList<Guid> ids, CancellationToken token) => DeleteArticlesAsync(ids, token);
        public Task CollectAsync(string url, string? model, IProgress<string>? progress, CancellationToken token) => Task.CompletedTask;
        public Task StartAsync() => Task.CompletedTask;
        public Task<WorkerStartupState> GetStartupAsync() => Task.FromResult(new WorkerStartupState(false, "検証用: サインイン時起動は未登録です。"));
        public Task<WorkerStartupState> SetStartupAsync(bool enabled) => GetStartupAsync();
    }
}
#endif
