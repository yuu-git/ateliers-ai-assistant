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
using Ateliers.Ai.Assistant.Desktop.ViewModels;
using Ateliers.Ai.Assistant.Infrastructure.Chat;
using Ateliers.Ai.Assistant.Infrastructure.Conversations;
using Ateliers.Ai.Assistant.Infrastructure.Storage;
using Ateliers.Ai.Assistant.Infrastructure.WebMonitoring;
using Microsoft.UI.Dispatching;

namespace Ateliers.Ai.Assistant.Desktop.Diagnostics;

/// <summary>実ウィンドウのバインディングと別プロセスでの Web 履歴復元を、隔離データで検証する。</summary>
internal static class WebMonitoringVerification
{
    /// <summary>実際の一括削除ダイアログを外部 UI Automation で操作するため、12記事の隔離ホストを作る。</summary>
    internal static MainWindow CreateDeletionInteractive(string root)
    {
        var paths = new AppDataPaths(Path.Combine(root, "r"), Path.Combine(root, "l"));
        var log = new FileDiagnosticLog(paths);
        var session = new ChatModelSession(new DummyChatService(), new EmptyControl());
        var queue = DispatcherQueue.GetForCurrentThread();
        void Dispatch(Action action) { if (queue.HasThreadAccess) action(); else queue.TryEnqueue(() => action()); }
        var settings = new SettingsViewModel(paths, new JsonUserSettingsStore(paths, log), log, _ => Task.CompletedTask);
        var store = new SqliteWebMonitoringStore(paths, log);
        var service = new WebCollectionService(new TestFeed { Count = 12 }, store, new ArticleMarkdownWriter(paths), session, log);
        var web = new WebMonitoringViewModel(service, store, session, session, settings, log, Dispatch, (_, _) => Task.CompletedTask);
        var window = new MainWindow(new ChatViewModel(session, session, Dispatch, new SqliteConversationStore(paths, log), log),
            new ModelManagementViewModel(session, Dispatch), session, settings, web);
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 1050));
        var started = false;
        window.Activated += async (_, _) =>
        {
            if (started) return; started = true;
            await window.InitializedForVerification;
            window.ShowWebForVerification(); await Task.Delay(300);
            window.SetWebInputForVerification("https://example.com/deletion.xml", "dummy");
            await web.FetchCommand.ExecuteAsync();
        };
        return window;
    }
    /// <summary>偽フィードと既存ダミーモデルを通常画面に接続する。</summary>
    internal static MainWindow Create(string reportPath, string root, bool resume)
    {
        const string feedUrl = "https://example.com/phase3.xml";
        var paths = new AppDataPaths(Path.Combine(root, "r"), Path.Combine(root, "l"));
        var log = new FileDiagnosticLog(paths);
        var session = new ChatModelSession(new DummyChatService(), new EmptyControl());
        var queue = DispatcherQueue.GetForCurrentThread();
        void Dispatch(Action action) { if (queue.HasThreadAccess) action(); else queue.TryEnqueue(() => action()); }
        var settings = new SettingsViewModel(paths, new JsonUserSettingsStore(paths, log), log, _ => Task.CompletedTask);
        var store = new SqliteWebMonitoringStore(paths, log);
        var source = new TestFeed();
        var web = new WebMonitoringViewModel(new WebCollectionService(source, store, new ArticleMarkdownWriter(paths), session, log),
            store, session, session, settings, log, Dispatch, (_, _) => Task.CompletedTask);
        var chat = new ChatViewModel(session, session, Dispatch, new SqliteConversationStore(paths, log), log);
        var window = new MainWindow(chat, new ModelManagementViewModel(session, Dispatch), session, settings, web);
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 1050));
        var report = new List<string>();
        var started = false; var passed = false;
        window.Closed += (_, _) =>
        {
            if (passed && resume && web.Runs.First().Run.Status != "中止") { passed = false; report.Add("FAIL: close did not finalize canceled collection"); }
            if (passed) { report.Add("PASS: normal native close, pending collection cancellation and bounded cleanup"); report.Add("PASS: ALL WEB DISPLAY CHECKS"); }
            File.WriteAllLines(reportPath, report);
        };
        window.Activated += async (_, _) =>
        {
            if (started) return; started = true;
            try
            {
                await window.InitializedForVerification.WaitAsync(TimeSpan.FromSeconds(20));
                window.ShowWebForVerification(); await Task.Delay(400);
                if (!resume)
                {
                    window.SetWebInputForVerification(feedUrl, "dummy"); await Task.Delay(100);
                    if (web.FeedUrl != feedUrl || web.SelectedModel != "dummy") throw new InvalidOperationException("Native input bindings did not update the ViewModel");
                    report.Add("PASS: native URL/model TwoWay bindings");
                }
                else if (web.FeedUrl != feedUrl || web.SelectedModel != "dummy" || web.Notifications.Count != 1)
                    throw new InvalidOperationException("Settings or notification did not survive process restart");
                await web.FetchCommand.ExecuteAsync();
                await web.FetchCommand.ExecuteAsync();
                await Task.Delay(200);
                if (web.HasError || window.WebCountsForVerification.Notifications != 1 || window.WebCountsForVerification.Runs != (resume ? 4 : 2))
                    throw new InvalidOperationException("Native result lists or deduplication failed: " + web.ErrorMessage);
                if (chat.Messages.Count != 0 || chat.InputText.Length != 0) throw new InvalidOperationException("Web summaries contaminated chat history");
                var notification = web.Notifications.Single();
                if (!File.ReadAllText(Path.Combine(paths.LocalDirectory, notification.Item.MarkdownPath)).Contains("実画面の RSS 記事"))
                    throw new InvalidOperationException("Artifact was not saved");
                report.Add("PASS: native history/notification lists, UTF8 Markdown and isolated chat history");
                if (!await window.NotificationPreviewForVerification.Ready.WaitAsync(TimeSpan.FromSeconds(20)))
                    throw new InvalidOperationException("Notification WebView2 is unavailable");
                window.SelectNotificationForVerification(0);
                await WaitPreview(window, "document.querySelectorAll('.message').length === 1 && document.querySelector('.body').innerText.includes('ダミー応答') && document.querySelector('.detail').textContent.includes('要約モデル: dummy')");
                const string markdown = "# 通知見出し\n\n**太字**\n\n- 一行目\n- 二行目\n\n|項目|値|\n|---|---|\n|日本語|表|\n\n```csharp\nvar total = 55;\n```\n\n<script>window.injected=true</script>\n\n[外部リンク](https://example.com/article)\n\n";
                var fullText = markdown + string.Concat(Enumerable.Repeat("\n\n長文の本文です。", 150)) + "\n\n末尾確認";
                var temporary = new WebNotificationViewModel(new ArticleNotification(new NotificationDto(Guid.NewGuid(), DateTimeOffset.UtcNow,
                    NotificationKind.Information, "通知プレビュー検証", fullText), "https://example.com/article", "unused.md"));
                web.Notifications.Add(temporary); window.SelectNotificationForVerification(web.Notifications.Count - 1);
                await WaitPreview(window, "document.querySelector('h1')?.textContent === '通知見出し' && document.querySelector('strong')?.textContent === '太字' && document.querySelectorAll('ul li').length === 2 && document.querySelector('table') !== null && document.querySelector('code[data-highlighted]') !== null && document.querySelector('.body').innerText.includes('末尾確認') && !window.injected && document.querySelector('.body script') === null && window.scrollY === 0");
                window.SelectNotificationForVerification(0);
                window.SelectNotificationForVerification(web.Notifications.Count - 1);
                window.SelectNotificationForVerification(0);
                await WaitPreview(window, "document.querySelector('.body')?.innerText.includes('ダミー応答') && !document.querySelector('.body')?.innerText.includes('末尾確認')");
                window.SelectNotificationForVerification(-1);
                await WaitPreview(window, "document.querySelectorAll('.message').length === 0");
                web.Notifications.Remove(temporary);
                report.Add("PASS: selected notification full Markdown, C# highlighting, safe HTML, top scroll, rapid selection and clearing");
                if (resume)
                {
                    report.Add("PASS: separate-process settings/history restoration and duplicate suppression");
                    source.Slow = true;
                    var collecting = web.FetchCommand.ExecuteAsync();
                    await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    if (!web.IsRunning || !session.IsBusy) throw new InvalidOperationException("Progress/exclusion state not exposed");
                }
                passed = true;
                RequestClose(window);
            }
            catch (Exception exception) { report.Add("FAIL: " + exception); File.WriteAllLines(reportPath, report); RequestClose(window); }
        };
        return window;
    }
    /// <summary>非同期終了処理を通る通常のシステム閉じる操作。</summary>
    private static void RequestClose(MainWindow window) => PostMessage(WinRT.Interop.WindowNative.GetWindowHandle(window), 0x0112, (nuint)0xF060, 0);
    /// <summary>選択した通知の最新世代が実 DOM に表示されるまで待つ。</summary>
    private static async Task WaitPreview(MainWindow window, string expression)
    {
        for (var attempt = 0; attempt < 150; attempt++)
        {
            if (await window.NotificationPreviewForVerification.CoreForVerification.ExecuteScriptAsync(expression) == "true") return;
            await Task.Delay(50);
        }
        throw new InvalidOperationException("Notification preview DOM check timed out: " + expression);
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
    /// <summary>安定した記事1件と、終了時中止のための待機を再現する。</summary>
    private sealed class TestFeed : IFeedSource
    {
        public int Count { get; set; } = 1;
        public bool Slow { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<FeedArticle>> FetchAsync(string url, CancellationToken cancellationToken)
        {
            if (Slow) { Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
            var source = FeedIdentity.Source(url);
            return Enumerable.Range(0, Count).Select(index => new FeedArticle(FeedIdentity.Key(source, "one-" + index, null, "", null), source, "one-" + index,
                "https://example.com/article/" + index, "実画面の RSS 記事 " + index, new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(index), "日本語の概要です。", "フィードの概要のみ")).ToArray();
        }
    }
    /// <summary>実サーバーの状態を変更しないモデル操作ポート。</summary>
    private sealed class EmptyControl : IModelControlService
    {
        public Task<IReadOnlyList<LoadedModel>> GetLoadedModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LoadedModel>>(Array.Empty<LoadedModel>());
        public Task LoadModelAsync(string model, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UnloadModelAsync(string model, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
#endif
