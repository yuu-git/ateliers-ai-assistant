using Ateliers.Ai.Assistant.Application.Models;
using Ateliers.Ai.Assistant.Application.Settings;
using Ateliers.Ai.Assistant.Application.Storage;
using Ateliers.Ai.Assistant.Application.WebMonitoring;
using Ateliers.Ai.Assistant.Desktop.ViewModels;
using Ateliers.Ai.Assistant.Infrastructure.Storage;
using Ateliers.Ai.Assistant.Infrastructure.WebMonitoring;

namespace Ateliers.Ai.Assistant.Desktop.Tests;

/// <summary>削除の副作用と確認設定の確定順序を実 SQLite・JSON と制御可能な保存先で検証する。</summary>
internal static class NotificationQuickActionTests
{
    public static async Task DeleteAndConfirmation()
    {
        using var f = new NotificationManagementTests.Fixture(); await f.SeedAsync(5, 5);
        var disk = new JsonUserSettingsStore(f.Paths, f.Log);
        var preferences = new SettingsViewModel(f.Paths, disk, f.Log, _ => Task.CompletedTask); await preferences.InitializeAsync();
        var session = new ChatModelSession(new TestChatService(), new TestModelControl());
        var real = new WebCollectionService(new NoFeed(), f.Store, new ArticleMarkdownWriter(f.Paths), session, f.Log);
        var operations = new DeleteProbe(real);
        using var web = new WebMonitoringViewModel(operations, f.Store, session, session, preferences, f.Log, a => a(), (_, _) => Task.CompletedTask, f.Store);
        await web.RefreshFeedAsync();
        var first = web.VisibleNotifications[0]; var second = web.VisibleNotifications[1];
        web.SelectedNotification = second;
        Check(first.UnreadSuffix == " ・ 未読" && first.BookmarkIcon == "☆" && first.BookmarkActionName == "ブックマークに追加", "unread suffix and accessible bookmark initial state");
        await web.ToggleBookmarkAsync(first);
        Check(first.BookmarkIcon == "★" && first.BookmarkActionName == "ブックマークを解除" && first.IsUnread, "bookmark does not select or read");
        var confirms = 0;
        web.ConfirmSingleArticleDeletionAsync = _ => { confirms++; return Task.FromResult(new ArticleDeletionDecision(false, true)); };
        await web.DeleteArticleAsync(first);
        Check(operations.Calls == 0 && preferences.ConfirmSingleArticleDeletion && web.Notifications.Count == 5 && ReferenceEquals(web.SelectedNotification, second), "cancel leaves articles, selection, unread and confirmation preference unchanged");
        operations.Fail = true;
        web.ConfirmSingleArticleDeletionAsync = _ => { confirms++; return Task.FromResult(new ArticleDeletionDecision(true, true)); };
        await web.DeleteArticleAsync(first);
        Check(preferences.ConfirmSingleArticleDeletion && web.HasError && web.Notifications.Count == 5 && first.IsUnread, "deletion failure does not suppress future confirmation");
        operations.Fail = false;
        var dialog = new TaskCompletionSource<ArticleDeletionDecision>();
        web.ConfirmSingleArticleDeletionAsync = _ => { confirms++; return dialog.Task; };
        var deleting = web.DeleteArticleAsync(first);
        await web.DeleteArticleAsync(first);
        Check(confirms == 3 && !web.CanDeleteArticle && !web.CanManageNotification && !web.DeleteArticlesCommand.CanExecute(null), "dialog blocks repeated single and bulk deletion and metadata operations");
        var markdown = Path.Combine(f.Paths.LocalDirectory, first.Item.MarkdownPath);
        Directory.CreateDirectory(Path.GetDirectoryName(markdown)!); await File.WriteAllTextAsync(markdown, "保存済み要約");
        dialog.SetResult(new(true, true)); await deleting;
        Check(!File.Exists(markdown) && web.TotalUnread == 4 && web.Notifications.Count == 4 && ReferenceEquals(web.SelectedNotification, second), $"single delete updates counts and file without selecting another article: file={File.Exists(markdown)}, unread={web.TotalUnread}, rows={web.Notifications.Count}, selected={ReferenceEquals(web.SelectedNotification, second)}, error={web.ErrorMessage}");
        Check(!preferences.ConfirmSingleArticleDeletion && !(await disk.LoadAsync()).ConfirmSingleArticleDeletion && (await f.Store.RunsAsync(default)).Count == 1, "preference commits after deletion and run history remains");
        await web.DeleteArticleAsync(second);
        Check(confirms == 3 && web.Notifications.Count == 3 && web.SelectedDocument is null && web.TotalUnread == 3, "suppressed confirmation deletes selected article and clears detail");
        var bulkConfirmed = 0;
        web.RequestArticleDeletionAsync = rows => { bulkConfirmed++; return Task.FromResult<IReadOnlyList<Guid>?>([rows[0].Item.Notification.Id]); };
        await web.DeleteArticlesCommand.ExecuteAsync();
        Check(bulkConfirmed == 1 && web.Notifications.Count == 2, "bulk confirmation remains independent");
        var reloaded = new SettingsViewModel(f.Paths, disk, f.Log, _ => Task.CompletedTask); await reloaded.InitializeAsync();
        Check(!reloaded.ConfirmSingleArticleDeletion, "restart restores suppression");
        await preferences.SetDeletionConfirmationAsync(true);
        Check((await disk.LoadAsync()).ConfirmSingleArticleDeletion, "settings can re-enable confirmation");
        await web.OpenArticleAsync(web.VisibleNotifications[0]);
        Check(web.SelectedNotification!.UnreadSuffix == "", "read article has no unread suffix");
    }
    public static async Task SettingsOrderingAndFailures()
    {
        using var f = new NotificationManagementTests.Fixture();
        var disk = new JsonUserSettingsStore(f.Paths, f.Log);
        Directory.CreateDirectory(f.Paths.SettingsDirectory);
        await File.WriteAllTextAsync(f.Paths.SettingsFile, "{\"LastSelectedModel\":\"old\"}");
        Check((await disk.LoadAsync()).ConfirmSingleArticleDeletion, "old JSON defaults to confirmation enabled");
        var probe = new SettingsProbe(disk);
        var settings = new SettingsViewModel(f.Paths, probe, f.Log, _ => Task.CompletedTask); await settings.InitializeAsync();
        probe.Gate = new TaskCompletionSource();
        var preference = settings.SetDeletionConfirmationAsync(false);
        var model = settings.RememberModelAsync("new-model");
        Check(settings.ConfirmSingleArticleDeletion && !settings.CanChangeDeletionConfirmation, "preference remains confirmed old value while storage is pending");
        probe.Gate.SetResult(); await preference; await model;
        Check(!(await disk.LoadAsync()).ConfirmSingleArticleDeletion && (await disk.LoadAsync()).LastSelectedModel == "new-model", "queued model save cannot overwrite successful preference save");
        await settings.SetDeletionConfirmationAsync(true); probe.Fail = true;
        Check(!await settings.SetDeletionConfirmationAsync(false) && settings.ConfirmSingleArticleDeletion && settings.HasError, "failed setting write preserves confirmed preference");
        probe.Fail = false; await settings.RememberModelAsync("another-model");
        Check((await disk.LoadAsync()).ConfirmSingleArticleDeletion, "later saves cannot persist failed suppression");
        await f.SeedAsync(1, 1);
        var session = new ChatModelSession(new TestChatService(), new TestModelControl());
        using var web = new WebMonitoringViewModel(new DeleteProbe(new WebCollectionService(new NoFeed(), f.Store, new ArticleMarkdownWriter(f.Paths), session, f.Log)), f.Store, session, session, settings, f.Log, a => a(), (_, _) => Task.CompletedTask, f.Store);
        await web.RefreshFeedAsync(); probe.Fail = true;
        web.ConfirmSingleArticleDeletionAsync = _ => Task.FromResult(new ArticleDeletionDecision(true, true));
        await web.DeleteArticleAsync(web.VisibleNotifications[0]);
        Check(web.Notifications.Count == 0 && settings.ConfirmSingleArticleDeletion && web.HasError && web.ErrorMessage.Contains("記事は削除"), "successful delete plus settings failure reports partial outcome and asks next time");
        Check((await disk.LoadAsync()).ConfirmSingleArticleDeletion, "settings disk preserved after post-delete failure");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class NoFeed : IFeedSource
    { public Task<IReadOnlyList<FeedArticle>> FetchAsync(string url, CancellationToken token) => throw new InvalidOperationException("Unexpected fetch"); }
    private sealed class DeleteProbe(IWebCollectionOperations inner) : IWebCollectionOperations
    {
        public int Calls; public bool Fail;
        public Task CollectAsync(string url, string? model, IProgress<string>? progress, CancellationToken token) => inner.CollectAsync(url, model, progress, token);
        public Task<ArticleDeletionResult> DeleteAsync(IReadOnlyList<Guid> ids, CancellationToken token)
        { Calls++; return Fail ? Task.FromResult(new ArticleDeletionResult([], ids, false, "検証用の削除失敗")) : inner.DeleteAsync(ids, token); }
    }
    private sealed class SettingsProbe(IUserSettingsStore inner) : IUserSettingsStore
    {
        public bool Fail; public TaskCompletionSource? Gate;
        public Task<UserSettings> LoadAsync(CancellationToken cancellationToken = default) => inner.LoadAsync(cancellationToken);
        public async Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default)
        { if (Gate is { } gate) await gate.Task; if (Fail) throw new StorageException("検証用の設定保存失敗"); await inner.SaveAsync(settings, cancellationToken); }
    }
}
