using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Ateliers.Ai.Assistant.Application.Chat;
using Ateliers.Ai.Assistant.Application.Models;
using Ateliers.Ai.Assistant.Application.Settings;
using Ateliers.Ai.Assistant.Application.Storage;
using Ateliers.Ai.Assistant.Application.WebMonitoring;
using Ateliers.Ai.Assistant.Core.Chat;
using Ateliers.Ai.Assistant.Desktop.ViewModels;
using Ateliers.Ai.Assistant.Infrastructure.Conversations;
using Ateliers.Ai.Assistant.Infrastructure.Storage;
using Ateliers.Ai.Assistant.Infrastructure.WebMonitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Ateliers.Ai.Assistant.Desktop.Tests;

/// <summary>外部サイトと実モデルに依存せず、実 DB・ファイル・偽 HTTP で収集の回復性を検証する。</summary>
internal static class WebMonitoringTests
{
    private const string Url = "https://example.com/feed.xml";
    private const string Rss = "<rss version='2.0'><channel><item><guid>first</guid><title>日本語の記事</title><link>https://example.com/a?id=1&amp;utm_source=feed</link><pubDate>Thu, 29 Oct 2026 12:00:00 GMT</pubDate><description><![CDATA[概要です。<b>外部データ</b>]]></description></item></channel></rss>";
    /// <summary>RSS・Atom の識別子、概要、公開日時、相対リンクと空フィード。</summary>
    public static async Task Parsing()
    {
        var rss = (await Fetch(Rss)).Single();
        Check(rss.Title == "日本語の記事" && rss.ExternalId == "first" && rss.Url == "https://example.com/a?id=1", "RSS normalization");
        Check(rss.PublishedAt?.Hour == 12 && rss.Content.Contains("概要") && rss.InputScope.Contains("概要のみ"), "RSS scope/date");
        var atom = (await Fetch("<feed xmlns='http://www.w3.org/2005/Atom'><entry><id>tag:example,2026:1</id><title>Atom</title><link href='/b'/><published>2026-10-29T10:00:00+09:00</published><content type='html'>&lt;p&gt;本文&lt;/p&gt;</content></entry></feed>")).Single();
        Check(atom.Url == "https://example.com/b" && atom.PublishedAt?.Hour == 1 && atom.InputScope.Contains("本文"), "Atom normalization");
        Check((await Fetch("<rss><channel/></rss>")).Count == 0, "empty valid feed");
        Check((await Fetch(Rss.Replace("</channel>", Rss[Rss.IndexOf("<item>")..Rss.IndexOf("</channel>")] + "</channel>"))).Count == 1, "in-feed duplicate");
    }
    /// <summary>既知の追跡パラメーターだけを削除し、記事や情報源を誤って統合しない。</summary>
    public static Task IdentityAndUrls()
    {
        var source = FeedIdentity.Source(Url + "?kind=a#fragment");
        Check(source.EndsWith("?kind=a"), "source query retained");
        Check(FeedIdentity.NormalizeArticleUrl("https://example.com/a?id=1&utm_medium=r&fbclid=x#f") == "https://example.com/a?id=1", "tracking removed");
        Check(FeedIdentity.Key(Url, null, "https://example.com/a?id=1", "one", null) != FeedIdentity.Key(Url, null, "https://example.com/a?id=2", "one", null), "identity query preserved");
        Check(FeedIdentity.Key(Url, "id", null, "a", null) == FeedIdentity.Key(Url, "id", "https://example.com/changed", "b", DateTimeOffset.Now), "updates do not change identity");
        Check(FeedIdentity.Key(Url, null, null, "same", null) == FeedIdentity.Key(Url, null, null, "same", null), "stable fallback");
        foreach (var url in new[] { "file:///C:/x", "ftp://example.com/a", "https://user:pw@example.com/", "http://localhost/a", "http://127.0.0.1/", "http://192.168.1.1/", "http://[::1]/", "http://printer.local/" })
            ExpectSync<WebMonitoringException>(() => FeedIdentity.ValidateUrl(url));
        Check(!FeedIdentity.IsPublicAddress(IPAddress.Parse("::ffff:10.0.0.1")) && !FeedIdentity.IsPublicAddress(IPAddress.Parse("169.254.169.254")) && FeedIdentity.IsPublicAddress(IPAddress.Parse("8.8.8.8")), "DNS address boundary");
        return Task.CompletedTask;
    }
    /// <summary>不正 XML、外部エンティティ、HTTP エラー、過大応答と内部宛てリダイレクトを拒否する。</summary>
    public static async Task FetchFailures()
    {
        await Expect<WebMonitoringException>(() => Fetch("<rss>"));
        await Expect<WebMonitoringException>(() => Fetch("<!DOCTYPE rss [<!ENTITY x SYSTEM 'file:///C:/secret'>]><rss><channel><item><title>&x;</title></item></channel></rss>"));
        await Expect<WebMonitoringException>(() => Fetch("<html/>"));
        using var error = new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        await Expect<WebMonitoringException>(() => new RssFeedSource(error).FetchAsync(Url, default));
        await Expect<WebMonitoringException>(() => Fetch(new string('a', RssFeedSource.MaximumBytes + 1)));
        using var redirect = new HttpClient(new StubHandler((_, _) => { var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri("http://127.0.0.1/private"); return Task.FromResult(response); }));
        await Expect<WebMonitoringException>(() => new RssFeedSource(redirect).FetchAsync(Url, default));
        using var timeout = new HttpClient(new StubHandler((_, _) => throw new TaskCanceledException()));
        await Expect<WebMonitoringException>(() => new RssFeedSource(timeout).FetchAsync(Url, default));
        using var slow = new HttpClient(new StubHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return null!; }));
        using var cancellation = new CancellationTokenSource(30);
        await Expect<OperationCanceledException>(() => new RssFeedSource(slow).FetchAsync(Url, cancellation.Token));
    }
    /// <summary>2回連続実行・新しいストアでの再実行でも、要約・通知・ファイルは各1件だけ。</summary>
    public static async Task DedupAndRestart()
    {
        using var fixture = new Fixture();
        await fixture.Service().CollectAsync(Url, "summary-model", null, default);
        await fixture.Service().CollectAsync(Url, "summary-model", null, default);
        var restarted = fixture.NewStore();
        await fixture.Service(restarted).CollectAsync(Url, "summary-model", null, default);
        var notifications = await restarted.NotificationsAsync(default);
        Check(notifications.Count == 1 && fixture.Chat.Calls == 1, "exactly once notification and summary");
        Check((await restarted.RunsAsync(default)).Count == 3 && (await restarted.RunsAsync(default)).First().Duplicates == 1, "persistent run counts");
        var notification = notifications.Single();
        var text = File.ReadAllText(Path.Combine(fixture.Paths.LocalDirectory, notification.MarkdownPath), Encoding.UTF8);
        Check(text.Contains("日本語の要約") && text.Contains("概要のみ") && text.Contains("summary-model") && text.Contains("取得日時"), "UTF8 metadata and input scope");
        Check(Directory.GetFiles(Path.Combine(fixture.Paths.LocalDirectory, "web-monitoring", "articles")).Length == 1, "one stable file without temp files");
        Check(fixture.Chat.Requests.All(messages => messages.Count == 1 && messages[0].Content.Contains("信頼できない")), "web prompts never receive chat history");
    }
    /// <summary>要約失敗と空応答を完了扱いにせず、再起動後に再試行する。</summary>
    public static async Task SummaryRetry()
    {
        using var fixture = new Fixture(); fixture.Chat.Fail = true;
        await fixture.Service().CollectAsync(Url, "summary-model", null, default);
        Check((await fixture.Store.NotificationsAsync(default)).Count == 0 && (await fixture.Store.RunsAsync(default)).Single().Failed == 1, "summary failure recorded");
        fixture.Chat.Fail = false; fixture.Chat.Empty = true;
        await fixture.Service(fixture.NewStore()).CollectAsync(Url, "summary-model", null, default);
        Check((await fixture.Store.PendingAsync(Url, default)).Single().Summary is null, "empty reply retryable");
        fixture.Chat.Empty = false;
        await fixture.Service(fixture.NewStore()).CollectAsync(Url, "summary-model", null, default);
        Check((await fixture.Store.NotificationsAsync(default)).Count == 1 && fixture.Chat.Calls == 3, "retry eventually exactly one notification");
    }
    /// <summary>ファイル書込失敗後は保存済み要約を再利用し、書込・通知確定を再試行する。</summary>
    public static async Task FileRetry()
    {
        using var fixture = new Fixture();
        var blockedDirectory = Path.Combine(fixture.Paths.LocalDirectory, "web-monitoring");
        Directory.CreateDirectory(fixture.Paths.LocalDirectory);
        File.WriteAllText(blockedDirectory, "A file blocks the artifact directory.");
        var writer = new ArticleMarkdownWriter(fixture.Paths);
        await fixture.Service(writer: writer).CollectAsync(Url, "summary-model", null, default);
        var pending = (await fixture.Store.PendingAsync(Url, default)).Single();
        Check(pending.Summary is not null && pending.MarkdownPath is null && !pending.Notified, "distinct summary and artifact stages");
        Check((await fixture.Store.NotificationsAsync(default)).Count == 0, "no notification before file");
        File.Delete(blockedDirectory);
        await fixture.Service(fixture.NewStore(), writer).CollectAsync(Url, "summary-model", null, default);
        Check(fixture.Chat.Calls == 1 && (await fixture.Store.NotificationsAsync(default)).Count == 1, "file-only retry reuses summary");
        await fixture.Store.CompleteAsync(pending with { MarkdownPath = ArticleMarkdownWriter.RelativePath(pending.Id), Notified = true }, default);
        Check((await fixture.Store.NotificationsAsync(default)).Count == 1, "idempotent final transaction");

        using var interrupted = new Fixture();
        await interrupted.Service(new InterruptCompletion(interrupted.Store)).CollectAsync(Url, "summary-model", null, default);
        var unfinished = (await interrupted.Store.PendingAsync(Url, default)).Single();
        Check(File.Exists(Path.Combine(interrupted.Paths.LocalDirectory, ArticleMarkdownWriter.RelativePath(unfinished.Id))) &&
            (await interrupted.Store.NotificationsAsync(default)).Count == 0, "file exists before failed DB completion");
        await interrupted.Service(interrupted.NewStore()).CollectAsync(Url, "summary-model", null, default);
        Check(interrupted.Chat.Calls == 1 && (await interrupted.Store.NotificationsAsync(default)).Count == 1, "restart after file/DB boundary reuses summary and creates one notification");
    }
    /// <summary>未選択・削除モデル・Ollama 停止と中止は実行履歴に残り、未完了記事を再試行できる。</summary>
    public static async Task ModelsAndCancel()
    {
        using var fixture = new Fixture();
        await fixture.Service().CollectAsync(Url, null, null, default);
        await fixture.Service().CollectAsync(Url, "missing", null, default);
        fixture.Chat.Unavailable = true;
        await fixture.Service().CollectAsync(Url, "summary-model", null, default);
        Check((await fixture.Store.RunsAsync(default)).All(run => run.Status == "失敗" && run.FinishedAt is not null), "model failures in history");
        fixture.Chat.Unavailable = false; fixture.Chat.Slow = true;
        using var token = new CancellationTokenSource();
        var running = fixture.Service().CollectAsync(Url, "summary-model", null, token.Token);
        await fixture.Chat.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        token.Cancel(); await running;
        Check((await fixture.Store.RunsAsync(default)).First().Status == "中止" && (await fixture.Store.PendingAsync(Url, default)).Count == 1, "canceled job preserves retry input");
        fixture.Chat.Slow = false;
        await fixture.Service().CollectAsync(Url, "summary-model", null, default);
        Check((await fixture.Store.NotificationsAsync(default)).Count == 1, "retry after cancellation");
    }
    /// <summary>収集全体の排他権により、記事間にも停止や別チャットを挟めない。</summary>
    public static async Task SharedModelExclusion()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = fixture.Session.RunExclusiveAsync(async (chat, token) => { entered.SetResult(); await release.Task; }, default);
        await entered.Task;
        Check(fixture.Session.IsBusy, "whole job is busy");
        await Expect<ChatServiceException>(() => fixture.Session.UnloadAsync("summary-model"));
        await Expect<ChatServiceException>(async () => { await foreach (var _ in fixture.Session.StreamReplyAsync("summary-model", [new(ChatRole.User, "chat")])) { } });
        release.SetResult(); await running;
        await fixture.Service().CollectAsync(Url, "summary-model", null, default);
        Check(fixture.Session.ManagedModels.Contains("summary-model"), "summary model tracked for app cleanup");
        fixture.Control.Running.Add(TestModelControl.Model("summary-model"));
        await fixture.Session.ShutdownAsync(TimeSpan.FromSeconds(2));
        Check(fixture.Control.Stopped.SequenceEqual(["summary-model"]), "only used model unloaded");
    }
    /// <summary>Phase 2 の DB に追加 Migration を適用しても、既存会話と下書きを保持する。</summary>
    public static async Task MigrationAndConversation()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Paths.DatabaseDirectory);
        var id = Guid.NewGuid();
        await using (var db = new AssistantDbContext(new DbContextOptionsBuilder<AssistantDbContext>().UseSqlite($"Data Source={fixture.Paths.DatabaseFile};Pooling=False").Options))
        {
            await db.GetService<IMigrator>().MigrateAsync("20261028185039_InitialConversations");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Conversations (Id,Title,CreatedUtcTicks,UpdatedUtcTicks,DraftText,Version) VALUES ({id},{"既存会話"},{DateTimeOffset.UtcNow.UtcTicks},{DateTimeOffset.UtcNow.UtcTicks},{"保存中の下書き"},{1L})");
        }
        await fixture.Service().CollectAsync(Url, "summary-model", null, default);
        var chatStore = new SqliteConversationStore(fixture.Paths, fixture.Log);
        var loaded = await chatStore.LoadAsync(id);
        Check(loaded?.DraftText == "保存中の下書き" && loaded.Title == "既存会話", "old chat preserved");
        await chatStore.DeleteAsync(id);
        Check((await fixture.Store.NotificationsAsync(default)).Count == 1, "chat deletion independent of web data");
    }
    /// <summary>Web 設定とチャット設定の連続保存が互いを消さず、不正 JSON は保護する。</summary>
    public static async Task SettingsAndViewModel()
    {
        using var fixture = new Fixture();
        var store = new JsonUserSettingsStore(fixture.Paths, fixture.Log);
        var settings = new SettingsViewModel(fixture.Paths, store, fixture.Log, _ => Task.CompletedTask);
        await settings.InitializeAsync();
        var webSave = settings.RememberWebAsync(Url, "summary-model");
        var chatSave = settings.RememberModelAsync("chat-model"); await Task.WhenAll(webSave, chatSave);
        var saved = await store.LoadAsync();
        Check(saved.WebFeedUrl == Url && saved.WebSummaryModel == "summary-model" && saved.LastSelectedModel == "chat-model", "merged settings");
        using var vm = new WebMonitoringViewModel(fixture.Service(), fixture.Store, fixture.Session, fixture.Session, settings, fixture.Log, action => action(), (_, _) => Task.CompletedTask);
        await vm.RefreshAsync(); vm.ApplySettings(saved.WebFeedUrl, saved.WebSummaryModel);
        Check(vm.SelectedModel == "summary-model" && fixture.Chat.Calls == 0, "settings restoration without inference");
        await vm.FetchCommand.ExecuteAsync();
        Check(vm.Notifications.Count == 1 && vm.Runs.Count == 1 && !vm.IsRunning && vm.CanEdit, "UI results refresh");
        await vm.FetchCommand.ExecuteAsync(); Check(vm.Notifications.Count == 1, "UI duplicate run");
        var notification = vm.Notifications.Single();
        vm.SelectedNotification = notification;
        Check(vm.SelectedDocument?.Content == notification.Item.Notification.MarkdownBody &&
            vm.SelectedDocument.Speaker == notification.Title && vm.SelectedDocument.Final, "selected preview keeps full Markdown without HTML or chat models");
        await vm.RefreshAsync();
        Check(vm.SelectedNotification?.Item.Notification.Id == notification.Item.Notification.Id && vm.SelectedDocument is not null, "refresh preserves selected document");
        vm.SelectedNotification = null; Check(vm.SelectedDocument is null, "deselection clears document");
        File.WriteAllText(fixture.Paths.SettingsFile, "{broken"); await settings.InitializeAsync();
        Check(!await settings.RememberWebAsync("https://example.com/other", null) && File.ReadAllText(fixture.Paths.SettingsFile) == "{broken", "corrupt settings protected");
    }
    /// <summary>通知の処理順に依存せず公開日時で全件を並べてから100件を選ぶ。既存 JSON も変更せず対象とする。</summary>
    public static async Task NotificationOrdering()
    {
        using var fixture = new Fixture();
        var sample = (await Fetch(Rss)).Single();
        var date = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var articles = Enumerable.Range(0, 102).Select(index => sample with {
            Key = FeedIdentity.Key(Url, "order-" + index, null, "", null), ExternalId = "order-" + index,
            Title = "記事 " + index, PublishedAt = index == 101 ? null : index == 1 ? date : date.AddDays(-index) }).ToArray();
        await fixture.Store.RegisterAsync(articles, DateTimeOffset.UtcNow, default);
        var pending = (await fixture.Store.PendingAsync(Url, default)).ToDictionary(article => article.Article.ExternalId!);
        foreach (var article in articles)
        {
            var current = pending[article.ExternalId!] with { Summary = "# 要約", Model = "model" };
            await fixture.Store.SaveArticleAsync(current, default);
            var path = await new ArticleMarkdownWriter(fixture.Paths).WriteAsync(current, default);
            await fixture.Store.CompleteAsync(current with { MarkdownPath = path }, default);
        }
        var notifications = await fixture.NewStore().NotificationsAsync(default);
        Check(notifications.Count == 100, "top 100 after sorting whole persistent history");
        Check(notifications[0].Notification.Title == "記事 101" && notifications[0].PublishedAt is null, "unknown publication date falls back to notification creation");
        Check(notifications[1].Notification.Title == "記事 1" && notifications[2].Notification.Title == "記事 0", "same publication date uses newest notification first");
        Check(notifications[^1].Notification.Title == "記事 98" && notifications.All(item => item.Notification.Title is not ("記事 99" or "記事 100")), "publication date beats reverse processing order before limit");
        Check(new WebNotificationViewModel(notifications[0]).Detail.Contains("公開日時不明") &&
            new WebNotificationViewModel(notifications[1]).Detail.StartsWith("公開 "), "date basis is visible");
    }
    /// <summary>記事を要約したモデルを永続データから表示し、後のモデル変更や重複取得で書き換えない。</summary>
    public static async Task NotificationModelHistory()
    {
        using var fixture = new Fixture();
        fixture.Chat.AvailableModels = ["summary-model", "second-model"];
        var articles = (await ManyArticles()).Take(2).ToArray();
        var source = new MutableFeed { Articles = [articles[0]] };
        var service = ServiceFor(fixture, source);
        await service.CollectAsync(Url, "summary-model", null, default);
        source.Articles = articles;
        await service.CollectAsync(Url, "second-model", null, default);
        var restored = await fixture.NewStore().NotificationsAsync(default);
        var first = restored.Single(item => item.Notification.Title == articles[0].Title);
        var second = restored.Single(item => item.Notification.Title == articles[1].Title);
        Check(first.SummaryModel == "summary-model" && second.SummaryModel == "second-model" && fixture.Chat.Calls == 2,
            "per-article actual model survives restart and duplicate collection with different model");
        Check(new WebNotificationViewModel(first).Detail.Contains("取得: RSS / Atom / 要約モデル: summary-model"), "retrieval is distinct from summarization");
        Check(new WebNotificationViewModel(first with { SummaryModel = null }).Detail.Contains("不明（記録なし）"), "missing historical model is not inferred from current selection");
    }
    /// <summary>Web の DB・Markdown・設定も環境の Local / Roaming に従う。</summary>
    public static async Task EnvironmentIsolation()
    {
        using var fixture = new Fixture();
        var root = fixture.Root;
        var debug = new AppDataPaths(Path.Combine(root, "r"), Path.Combine(root, "l"), AppDataEnvironment.Debug);
        var release = new AppDataPaths(Path.Combine(root, "r"), Path.Combine(root, "l"), AppDataEnvironment.Release);
        var article = new CollectedArticle(Guid.NewGuid(), (await Fetch(Rss)).Single(), DateTimeOffset.UtcNow, "環境別の要約", "model");
        var relative = await new ArticleMarkdownWriter(debug).WriteAsync(article, default);
        Check(File.Exists(Path.Combine(debug.LocalDirectory, relative)) && !Directory.Exists(release.LocalDirectory), "Markdown environment separation");
        await new SqliteWebMonitoringStore(debug, fixture.Log).SaveRunAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow), default);
        Check((await new SqliteWebMonitoringStore(release, fixture.Log).RunsAsync(default)).Count == 0 && !File.Exists(release.DatabaseFile), "DB environment separation and lazy reads");
    }
    /// <summary>公開日時の新しい10件だけを処理し、範囲外の未完了記事を勝手に再試行しない。</summary>
    public static async Task LatestTen()
    {
        using var fixture = new Fixture();
        var source = new MutableFeed { Articles = await ManyArticles() };
        var latest = FeedSelection.Latest(source.Articles);
        Check(latest.Count == 10 && latest[0].Title == "対象 11" && latest[^1].Title == "対象 2", "newest ten rather than first ten");
        var unknown = source.Articles.Select(article => article with { PublishedAt = null }).ToArray();
        Check(FeedSelection.Latest(unknown).Select(article => article.Key).SequenceEqual(unknown.Take(10).Select(article => article.Key)), "unknown dates preserve feed order");
        fixture.Chat.Fail = true;
        var service = ServiceFor(fixture, source);
        await service.CollectAsync(Url, "summary-model", null, default);
        Check(fixture.Chat.Calls == 10 && (await fixture.Store.RunsAsync(default)).Single().Fetched == 10, "only ten attempts recorded");
        fixture.Chat.Fail = false;
        source.Articles = latest.Where(article => article.Title != "対象 2").ToArray();
        await service.CollectAsync(Url, "summary-model", null, default);
        Check(fixture.Chat.Calls == 19 && (await fixture.Store.NotificationsAsync(default)).Count == 9, "retry only items still in current range");
        Check((await fixture.Store.PendingAsync(Url, default)).Single().Article.Title == "対象 2", "older incomplete article retained without automatic retry");
    }
    /// <summary>複数削除後は重複キー・ファイル・通知が消え、フィード内の対象だけが新規として復活する。</summary>
    public static async Task BulkDeleteAndRestore()
    {
        using var fixture = new Fixture();
        var source = new MutableFeed { Articles = await ManyArticles() };
        var service = ServiceFor(fixture, source);
        await service.CollectAsync(Url, "summary-model", null, default);
        var before = await fixture.Store.NotificationsAsync(default);
        var targets = before.Where(item => item.Notification.Title is "対象 5" or "対象 8").ToArray();
        var ids = targets.Select(item => item.Notification.Id).ToArray();
        var result = await service.DeleteAsync([ids[0], ids[1], ids[0]], default);
        Check(result.DeletedIds.Count == 2 && result.FailedIds.Count == 0 && (await fixture.NewStore().NotificationsAsync(default)).Count == 8, "bulk deletion and duplicate ID handling");
        Check(targets.All(item => !File.Exists(Path.Combine(fixture.Paths.LocalDirectory, item.MarkdownPath))), "deleted artifacts removed");
        Check(before.Except(targets).All(item => File.Exists(Path.Combine(fixture.Paths.LocalDirectory, item.MarkdownPath))) &&
            (await fixture.Store.RunsAsync(default)).Count == 1, "other artifacts and historical run retained");
        source.Articles = FeedSelection.Latest(source.Articles).Where(article => article.Title != "対象 5").ToArray();
        await service.CollectAsync(Url, "summary-model", null, default);
        var restored = await fixture.Store.NotificationsAsync(default);
        Check(restored.Count == 9 && !restored.Any(item => item.Notification.Title == "対象 5") &&
            restored.Single(item => item.Notification.Title == "対象 8").Notification.Id != targets.Single(item => item.Notification.Title == "対象 8").Notification.Id, "only deleted item still in feed restored with new identity");
        Check((await fixture.Store.RunsAsync(default)).First().New == 1 && fixture.Chat.Calls == 11, "nine retained article keys prevent duplicate summaries");
        var single = restored.First();
        await service.DeleteAsync([single.Notification.Id], default);
        Check((await fixture.Store.NotificationsAsync(default)).Count == 8, "single deletion still supported");
        await service.DeleteAsync((await fixture.Store.NotificationsAsync(default)).Select(item => item.Notification.Id).ToArray(), default);
        Check((await fixture.Store.NotificationsAsync(default)).Count == 0 && (await fixture.Store.RunsAsync(default)).Count == 2, "all selection clears only articles and preserves run history");
        await service.CollectAsync(Url, "summary-model", null, default);
        Check((await fixture.Store.RunsAsync(default)).First().New == 9 && (await fixture.Store.NotificationsAsync(default)).Count == 9, "all deleted current entries are new on next fetch");
    }
    /// <summary>削除不可のファイルでは DB を保持し、他の対象は削除する。欠損ファイルでも再試行できる。</summary>
    public static async Task DeleteFailures()
    {
        using var fixture = new Fixture();
        var source = new MutableFeed { Articles = (await ManyArticles()).Take(2).ToArray() };
        var service = ServiceFor(fixture, source);
        await service.CollectAsync(Url, "summary-model", null, default);
        var items = await fixture.Store.NotificationsAsync(default);
        var locked = items[0]; var removable = items[1];
        var lockedPath = Path.Combine(fixture.Paths.LocalDirectory, locked.MarkdownPath);
        using (var file = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await service.DeleteAsync([locked.Notification.Id, removable.Notification.Id], default);
            Check(result.FailedIds.SequenceEqual([locked.Notification.Id]) && result.DeletedIds.SequenceEqual([removable.Notification.Id]) && result.Error.Length != 0, "partial failure result");
            Check((await fixture.Store.NotificationsAsync(default)).Single().Notification.Id == locked.Notification.Id && File.Exists(lockedPath), "file failure rolls back article and cascaded notification");
        }
        File.Delete(lockedPath);
        var retry = await service.DeleteAsync([locked.Notification.Id], default);
        Check(retry.FailedIds.Count == 0 && (await fixture.Store.NotificationsAsync(default)).Count == 0, "missing file can still delete metadata");
        await fixture.Store.DeleteArticleAsync(locked.Notification.Id, default); // 再削除も成功扱い。
        await Expect<WebMonitoringException>(() => service.DeleteAsync([Guid.Empty], default));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Expect<OperationCanceledException>(() => service.DeleteAsync([Guid.NewGuid()], canceled.Token));
    }
    /// <summary>選択確認のキャンセル・複数対象・成功後のプレビュー消去と新規再取得を検証する。</summary>
    public static async Task DeleteViewModel()
    {
        using var fixture = new Fixture();
        var settings = new SettingsViewModel(fixture.Paths, new JsonUserSettingsStore(fixture.Paths, fixture.Log), fixture.Log, _ => Task.CompletedTask);
        await settings.InitializeAsync();
        using var vm = new WebMonitoringViewModel(fixture.Service(), fixture.Store, fixture.Session, fixture.Session, settings, fixture.Log, action => action(), (_, _) => Task.CompletedTask);
        await vm.RefreshAsync(); vm.ApplySettings(Url, "summary-model");
        Check(!vm.DeleteArticlesCommand.CanExecute(null), "no article deletion when empty");
        await vm.FetchCommand.ExecuteAsync();
        var item = vm.Notifications.Single(); vm.SelectedNotification = item;
        vm.RequestArticleDeletionAsync = _ => Task.FromResult<IReadOnlyList<Guid>?>(null);
        await vm.DeleteArticlesCommand.ExecuteAsync();
        Check(vm.Notifications.Count == 1 && vm.SelectedDocument is not null, "dialog cancellation preserves selection and records");
        vm.RequestArticleDeletionAsync = visible => Task.FromResult<IReadOnlyList<Guid>?>(visible.Select(row => row.Item.Notification.Id).ToArray());
        await vm.DeleteArticlesCommand.ExecuteAsync();
        Check(vm.Notifications.Count == 0 && vm.SelectedDocument is null && !vm.IsRunning && !vm.HasError, "confirmed selection clears preview and enables fetching");
        await vm.FetchCommand.ExecuteAsync();
        Check(vm.Notifications.Single().Item.Notification.Id != item.Item.Notification.Id && vm.Runs.First().Run.New == 1, "deleted item is newly collected");
    }
    private static async Task<IReadOnlyList<FeedArticle>> ManyArticles()
    {
        var sample = (await Fetch(Rss)).Single();
        return Enumerable.Range(0, 12).Select(index => sample with { Key = FeedIdentity.Key(Url, "many-" + index, null, "", null),
            ExternalId = "many-" + index, Title = "対象 " + index, PublishedAt = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(index) }).ToArray();
    }
    private static WebCollectionService ServiceFor(Fixture fixture, IFeedSource source) => new(source, fixture.Store, new ArticleMarkdownWriter(fixture.Paths), fixture.Session, fixture.Log);
    private sealed class MutableFeed : IFeedSource
    {
        public IReadOnlyList<FeedArticle> Articles { get; set; } = [];
        public Task<IReadOnlyList<FeedArticle>> FetchAsync(string url, CancellationToken cancellationToken) => Task.FromResult(Articles);
    }
    private static async Task<IReadOnlyList<FeedArticle>> Fetch(string xml)
    {
        using var client = new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") })));
        return await new RssFeedSource(client).FetchAsync(Url, default);
    }
    private static void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); }
    private static async Task Expect<T>(Func<Task> work) where T : Exception { try { await work(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void ExpectSync<T>(Action work) where T : Exception { try { work(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ateliers-phase3-tests-" + Guid.NewGuid().ToString("N"));
        public AppDataPaths Paths { get; }
        public FileDiagnosticLog Log { get; }
        public SqliteWebMonitoringStore Store { get; }
        public SummaryChat Chat { get; } = new();
        public TestModelControl Control { get; } = new();
        public ChatModelSession Session { get; }
        public Fixture() { Paths = new(Path.Combine(Root, "r"), Path.Combine(Root, "l")); Log = new(Paths); Store = NewStore(); Session = new(Chat, Control); }
        public SqliteWebMonitoringStore NewStore() => new(Paths, Log);
        public WebCollectionService Service(IWebMonitoringStore? store = null, IArticleMarkdownWriter? writer = null) => new(new SampleFeed(), store ?? Store, writer ?? new ArticleMarkdownWriter(Paths), Session, Log);
        public void Dispose()
        {
            var full = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(full) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(full).StartsWith("ateliers-phase3-tests-", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe temp path.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
    private sealed class SampleFeed : IFeedSource
    {
        public Task<IReadOnlyList<FeedArticle>> FetchAsync(string url, CancellationToken cancellationToken) => Fetch(Rss);
    }
    private sealed class SummaryChat : IChatService
    {
        public IReadOnlyList<string> AvailableModels { get; set; } = ["summary-model"];
        public bool Fail, Empty, Slow, Unavailable;
        public int Calls;
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken = default) => Unavailable ? throw new ChatServiceException("unavailable") : Task.FromResult(AvailableModels);
        public async IAsyncEnumerable<string> StreamReplyAsync(string model, IReadOnlyList<ChatMessage> messages, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++; Requests.Add(messages); Started.TrySetResult();
            if (Slow) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (Fail) throw new ChatServiceException("summary failure");
            if (!Empty) yield return "日本語の要約です。";
        }
    }
    private sealed class InterruptCompletion(IWebMonitoringStore inner) : IWebMonitoringStore
    {
        public Task DeleteArticleAsync(Guid id, CancellationToken token) => inner.DeleteArticleAsync(id, token);
        public Task SaveRunAsync(CollectionRun run, CancellationToken token) => inner.SaveRunAsync(run, token);
        public Task<int> RegisterAsync(IReadOnlyList<FeedArticle> articles, DateTimeOffset fetchedAt, CancellationToken token) => inner.RegisterAsync(articles, fetchedAt, token);
        public Task<IReadOnlyList<CollectedArticle>> PendingAsync(string source, CancellationToken token) => inner.PendingAsync(source, token);
        public Task SaveArticleAsync(CollectedArticle article, CancellationToken token) => inner.SaveArticleAsync(article, token);
        public Task CompleteAsync(CollectedArticle article, CancellationToken token) => throw new StorageException("Simulated crash boundary after file write.");
        public Task<IReadOnlyList<CollectionRun>> RunsAsync(CancellationToken token) => inner.RunsAsync(token);
        public Task<IReadOnlyList<ArticleNotification>> NotificationsAsync(CancellationToken token) => inner.NotificationsAsync(token);
    }
}
