#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ateliers.Ai.Assistant.Desktop.ViewModels;
using Ateliers.Ai.Assistant.Infrastructure.Worker;

namespace Ateliers.Ai.Assistant.Desktop.Diagnostics;

/// <summary>独立した偽 RSS/Ollama Worker と実 XAML をつなぎ、通知管理を通常データから隔離して検証する。</summary>
internal static class NotificationFeatureVerification
{
    internal static async Task RunAsync(MainWindow window, WebMonitoringViewModel web, WorkerViewModel worker, WorkerApiClient api, List<string> report)
    {
        if (web.TotalUnread != 0) throw new InvalidOperationException("Pending articles counted as unread");
        await window.GlobalStatusForVerification.ToggleAiForVerificationAsync();
        await UntilAsync(async () => { await worker.PollAsync(); return web.Notifications.Count == 10; }, "ten completed notifications");
        if (web.TotalUnread != 10 || web.NotificationGroups.Count == 0 || window.UnreadCountForVerification != 10 || web.SourceChoices[0].UnreadCount != 10) throw new InvalidOperationException("Unread count, native badge or date groups missing");
        var view = window.WebForVerification; view.SelectSourceForVerification(1); await web.RefreshFeedAsync();
        var first = web.VisibleNotifications[0]; var second = web.VisibleNotifications[1]; var acquired = first.Item.FetchedAt;
        window.SelectNotificationForVerification(0); await worker.PollAsync();
        if (!first.IsUnread || web.TotalUnread != 10) throw new InvalidOperationException("Automatic selection marked read");
        // 未読一覧で詳細を開いても残ることと、明示的な未読戻しを実 XAML で確認する。
        view.SelectViewForVerification(1); await web.RefreshFeedAsync();
        await view.InvokeArticleForVerificationAsync(0, bookmark: false); await worker.PollAsync();
        view.VerifyCurrentArticleForVerification();
        if (!first.IsUnread || web.TotalUnread != 10 || web.SelectedDocument is null) throw new InvalidOperationException("Unread article disappeared while reading");
        await web.ToggleReadCommand.ExecuteAsync();
        if (web.TotalUnread != 9 || web.SelectedNotification?.Item.Notification.Id != first.Item.Notification.Id || web.SelectedDocument is null)
            throw new InvalidOperationException("Explicit read cleared detail through hidden legacy list");
        await web.ToggleReadCommand.ExecuteAsync();
        await view.InvokeArticleForVerificationAsync(1, bookmark: false); await web.EndReadingAsync();
        view.VerifyCurrentArticleForVerification();
        var persisted = await api.QueryAsync(new());
        if (persisted.Items.Single(row => row.Notification.Id == first.Item.Notification.Id).ReadAt is not null || persisted.TotalUnread != 9)
            throw new InvalidOperationException("Manual unread lost on departure or departed article was not read");
        await api.SetReadAsync(new(second.Item.Notification.Id, false)); await web.RefreshFeedAsync();
        view.SelectViewForVerification(0); await web.RefreshFeedAsync();
        first = web.VisibleNotifications[0]; second = web.VisibleNotifications[1];
        report.Add("PASS: native unread detail retained, explicit read does not clear detail, manual unread wins and deselection persists read");
        report.Add("PASS: current article highlight follows detail selection across polling, switching and closing");
        await view.InvokeArticleForVerificationAsync(0, bookmark: false);
        if (web.TotalUnread != 10 || !first.IsUnread) throw new InvalidOperationException("Open marked read before departure");
        await web.EndReadingAsync(); await view.InvokeArticleForVerificationAsync(0, bookmark: false);
        await UntilAsync(async () => { await web.RefreshFeedAsync(); return web.TotalUnread == 9; }, "departed article read");
        var preview = view.PreviewForVerification;
        await preview.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(300);
        await preview.CoreForVerification.ExecuteScriptAsync("window.scrollTo(0,100)");
        var scroll = await preview.CoreForVerification.ExecuteScriptAsync("window.scrollY");
        await view.InvokeArticleForVerificationAsync(1, bookmark: true);
        await UntilAsync(async () => { await web.RefreshFeedAsync(); return second.Item.IsBookmarked && web.CanManageNotification; }, "bookmark confirmation");
        if (!second.IsUnread || web.TotalUnread != 9 || !ReferenceEquals(first, web.SelectedNotification) || await preview.CoreForVerification.ExecuteScriptAsync("window.scrollY") != scroll)
            throw new InvalidOperationException("Bookmark selected/read another article or reset detail");
        await web.ToggleReadCommand.ExecuteAsync();
        await UntilAsync(async () => { await web.RefreshFeedAsync(); return first.IsUnread && web.TotalUnread == 10 && web.CanManageNotification; }, "return to unread before tags");
        await view.AddTagForVerificationAsync(" C# ");
        await view.AddTagForVerificationAsync("調査");
        await UntilAsync(async () => { await web.RefreshFeedAsync(); return first.TagItems.Count == 2 && web.TotalUnread == 10; }, "tags and unread restored");
        if (first.Item.FetchedAt != acquired || !first.IsUnread) throw new InvalidOperationException("Metadata changed acquisition or read");
        await web.FilterByTagAsync(first.TagItems[0]);
        await UntilAsync(async () => { await web.RefreshFeedAsync(); return web.VisibleNotifications.Count == 1; }, "tag filter");
        await web.ClearNotificationFiltersCommand.ExecuteAsync();
        view.SelectViewForVerification(2);
        await UntilAsync(async () => { await web.RefreshFeedAsync(); return web.VisibleNotifications.Count == 1 && web.VisibleNotifications[0].Item.IsBookmarked; }, "bookmark query");
        await view.InvokeArticleForVerificationAsync(0, bookmark: true);
        await UntilAsync(async () => { await web.RefreshFeedAsync(); return web.VisibleNotifications.Count == 0; }, "bookmark removal under filter");
        if (web.TotalUnread != 10) throw new InvalidOperationException("Bookmark removal read article");
        await web.ClearNotificationFiltersCommand.ExecuteAsync();
        await UntilAsync(async () => { await web.RefreshFeedAsync(); return web.VisibleNotifications.Count == 10; }, "all query restored");
        worker.Model = "未保存のモデル入力";
        await window.GlobalStatusForVerification.ToggleAiForVerificationAsync();
        if (!worker.ActualAiPaused || worker.Model != "未保存のモデル入力" || (await api.GetStatusAsync()).Configuration.SummaryModel != "verification-model")
            throw new InvalidOperationException("Global pause saved an unrelated draft");
        var ids = web.Notifications.Select(row => row.Item.Notification.Id).ToArray();
        await worker.StopCommand.ExecuteAsync();
        await UntilAsync(async () => { await worker.PollAsync(); return worker.GlobalWorkerState.Contains("停止中"); }, "actual process stopped");
        await worker.StartCommand.ExecuteAsync();
        await UntilAsync(async () => { await worker.PollAsync(); return worker.Connected && web.Notifications.Count == 10; }, "worker restart and query");
        if (!worker.ActualAiPaused || web.TotalUnread != 10 || !web.Notifications.Any(row => row.TagItems.Count == 2) || web.Notifications.Any(row => !ids.Contains(row.Item.Notification.Id)))
            throw new InvalidOperationException("Restart lost pause, metadata or identity");
        foreach (var width in new[] { 1500, 1000, 520 }) { window.AppWindow.Resize(new(width, 1050)); await Task.Delay(100); }
        report.Add("PASS: native grouped notifications, explicit read only, independent bookmark button, tag add/filter, global pause without draft save and Worker stop/restart persistence");
    }
    private static async Task UntilAsync(Func<Task<bool>> check, string label)
    { for (var attempt = 0; attempt < 150; attempt++) { if (await check()) return; await Task.Delay(150); } throw new TimeoutException(label); }
}
#endif
