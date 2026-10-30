using System.Net;
using System.Text;
using System.Text.Json;
using Ateliers.Ai.Assistant.Application.Models;
using Ateliers.Ai.Assistant.Application.WebMonitoring;
using Ateliers.Ai.Assistant.Application.Worker;
using Ateliers.Ai.Assistant.Infrastructure.Storage;
using Ateliers.Ai.Assistant.Infrastructure.WebMonitoring;
using Ateliers.Ai.Assistant.Infrastructure.Worker;
using Ateliers.Ai.Assistant.Desktop.ViewModels;

namespace Ateliers.Ai.Assistant.Desktop.Tests;

/// <summary>GitHub の応答を再現し、実 SQLite・Worker へつないで外部通信なしで検証する。</summary>
internal static class GitHubReleaseTests
{
    /// <summary>明示指定した公開 API を1回だけ読む。Ollama・設定・DB・通常データは触らない。</summary>
    public static async Task LiveFetch(string repository)
    {
        using var client = new HttpClient(RssFeedSource.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        var items = await new GitHubReleasesSource(client).FetchAsync(Source(repository), default);
        Check(items.Count is > 0 and <= 10 && items.All(item => item.GitHubRelease is { Draft: false, Prerelease: false }), "live public release metadata");
        Console.WriteLine($"PASS: live GitHub API {ContentSourceIdentity.Repository(repository)}, selected {items.Count}, newest Release ID {items[0].GitHubRelease!.ReleaseId}; no persistence or Ollama calls");
    }
    public static async Task FetchContractAndSelection()
    {
        var calls = 0;
        using var client = Client((request, _) =>
        {
            calls++;
            Check(request.RequestUri!.AbsoluteUri == "https://api.github.com/repos/microsoft/vscode/releases?per_page=100&page=1", "fixed host, normalized repository and bounded page");
            Check(request.Headers.UserAgent.Count != 0 && request.Headers.Contains("X-GitHub-Api-Version") && request.Headers.Accept.Any(value => value.MediaType == "application/vnd.github+json") && request.Headers.Authorization is null && !request.Headers.Contains("If-None-Match"), "headers without credentials or unsupported conditional cache");
            return Task.FromResult(Json(Enumerable.Range(1, 16).Select(i => Release(i, pre: i == 16, draft: i == 15, body: new string('本', 13000))).ToArray()));
        });
        var reader = new GitHubReleasesSource(client);
        var source = Source("Microsoft/VSCode");
        var items = await reader.FetchAsync(source, default);
        Check(items.Count == 10 && items[0].GitHubRelease!.ReleaseId == 14 && items[^1].GitHubRelease!.ReleaseId == 5 && calls == 1, "exclude draft and prerelease before published-time take10, no extra pages");
        Check(items.All(item => item.Content.Length == 12000 && item.InputScope.Contains("12,000") && item.Source == "https://api.github.com/repos/microsoft/vscode/releases") && items[0].Url!.StartsWith("https://github.com/"), "bounded input and source/article URL separation");
        var withPre = await reader.FetchAsync(source with { IncludePrereleases = true }, default);
        Check(withPre[0].GitHubRelease!.ReleaseId == 16 && withPre.All(item => !item.GitHubRelease!.Draft), "prerelease opt-in, draft always excluded");
        var same = await reader.FetchAsync(Source("microsoft/vscode"), default);
        Check(same.Select(item => item.Key).SequenceEqual(items.Select(item => item.Key)), "repository case is same identity");
        using var otherClient = Client((_, _) => Task.FromResult(Json([Release(14)])));
        var other = (await new GitHubReleasesSource(otherClient).FetchAsync(Source("other/project"), default)).Single();
        Check(other.Key != items[0].Key, "same release/tag in another repository is not merged");
        foreach (var invalid in new[] { "", "https://github.com/microsoft/vscode", "owner/repo/extra", "owner/..", "owner/repo?x=1", "owner\\repo" })
            await Expect<WebMonitoringException>(() => Task.FromResult(ContentSourceIdentity.Repository(invalid)));
    }
    public static async Task FailuresAndBounds()
    {
        var clock = new Clock();
        foreach (var (code, expected) in new[] { (404, "404"), (403, "403"), (500, "500"), (401, "401") })
        {
            using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent("secret-response-body") }));
            var failure = await Expect<GitHubFetchException>(() => new GitHubReleasesSource(client, clock).FetchAsync(Source(), default));
            Check(failure.Message.Contains(expected) && !failure.Message.Contains("secret") && failure.RetryAt is null, "classified safe HTTP error");
        }
        foreach (var code in new[] { 403, 429 })
        {
            using var client = Client((_, _) =>
            {
                var response = new HttpResponseMessage((HttpStatusCode)code);
                response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
                response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", clock.GetUtcNow().AddHours(1).ToUnixTimeSeconds().ToString()); return Task.FromResult(response);
            });
            Check((await Expect<GitHubFetchException>(() => new GitHubReleasesSource(client, clock).FetchAsync(Source(), default))).RetryAt == clock.GetUtcNow().AddHours(1), "primary rate reset in UTC");
        }
        using (var client = Client((_, _) => { var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests); response.Headers.RetryAfter = new(TimeSpan.FromMinutes(7)); return Task.FromResult(response); }))
            Check((await Expect<GitHubFetchException>(() => new GitHubReleasesSource(client, clock).FetchAsync(Source(), default))).RetryAt == clock.GetUtcNow().AddMinutes(7), "Retry-After takes precedence");
        using (var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"message\":\"You have exceeded a secondary rate limit. secret detail\"}") })))
        {
            var failure = await Expect<GitHubFetchException>(() => new GitHubReleasesSource(client, clock).FetchAsync(Source(), default));
            Check(failure.RetryAt == clock.GetUtcNow().AddMinutes(5) && !failure.Message.Contains("secret"), "secondary limit without headers waits and hides original message");
        }
        foreach (var bad in new[] { "null", "{}", "[{}]", "[null]", "not json", "[{\"id\":1,\"draft\":false,\"prerelease\":false,\"tag_name\":\"v1\",\"published_at\":\"invalid\"}]" })
        {
            using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(bad) }));
            await Expect<GitHubFetchException>(() => new GitHubReleasesSource(client).FetchAsync(Source(), default));
        }
        using (var client = Client((_, _) => { var response = Json([]); response.Content.Headers.ContentLength = GitHubReleasesSource.MaximumBytes + 1; return Task.FromResult(response); }))
            await Expect<GitHubFetchException>(() => new GitHubReleasesSource(client).FetchAsync(Source(), default));
        using (var client = Client((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("private detail"))))
            Check(!(await Expect<GitHubFetchException>(() => new GitHubReleasesSource(client).FetchAsync(Source(), default))).Message.Contains("private"), "network error does not leak details");
        using (var client = Client((_, _) => Task.FromException<HttpResponseMessage>(new TaskCanceledException())))
            Check((await Expect<GitHubFetchException>(() => new GitHubReleasesSource(client).FetchAsync(Source(), default))).Message.Contains("タイムアウト"), "internal timeout is a failure");
        using (var cancel = new CancellationTokenSource())
        using (var client = Client(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Json([]); }))
        { cancel.CancelAfter(20); await Expect<OperationCanceledException>(() => new GitHubReleasesSource(client).FetchAsync(Source(), cancel.Token)); }
    }
    public static async Task RedirectAndEmpty()
    {
        var calls = 0;
        using (var client = Client((_, _) => { calls++; var response = new HttpResponseMessage(HttpStatusCode.MovedPermanently); response.Headers.Location = new Uri("https://evil.example/collect"); return Task.FromResult(response); }))
        { await Expect<GitHubFetchException>(() => new GitHubReleasesSource(client).FetchAsync(Source(), default)); Check(calls == 1, "foreign redirect never followed"); }
        calls = 0;
        using (var client = Client((_, _) => { calls++; if (calls > 1) return Task.FromResult(Json([])); var response = new HttpResponseMessage(HttpStatusCode.MovedPermanently); response.Headers.Location = new Uri("https://api.github.com/repositories/123/releases?per_page=100&page=1"); return Task.FromResult(response); }))
            Check((await new GitHubReleasesSource(client).FetchAsync(Source(), default)).Count == 0 && calls == 2, "same API redirect and valid empty result");
        using (var client = Client((_, _) => Task.FromResult(Json([Release(1, title: null, body: null)]))))
        { var item = (await new GitHubReleasesSource(client).FetchAsync(Source(), default)).Single(); Check(item.Title == "v1" && item.Content == "", "title fallback and empty release notes"); }
    }
    public static async Task WorkerPersistenceAndMetadata()
    {
        using var f = new NotificationManagementTests.Fixture(); await f.SeedAsync(1, 1);
        var oldId = (await f.Store.QueryAsync(new())).Items.Single().Notification.Id;
        var edited = false;
        using var client = Client((_, _) => Task.FromResult(Json([Release(1, body: edited ? "編集された本文" : "原文"), Release(2)])));
        var chat = new TestChatService(); var models = new TestModelControl(); models.Running.Add(TestModelControl.Model("model-a"));
        var settings = new WorkerStateStore(f.Paths, f.Store, new JsonUserSettingsStore(f.Paths, f.Log));
        var source = Source(); var clock = new Clock();
        WorkerCoordinator Create() => new(settings, f.Store, new ContentSourceRouter(new RssStub(), new GitHubReleasesSource(client, clock)), new ArticleMarkdownWriter(f.Paths), chat, models, FileOperationGate.ForModels(f.Paths), f.Log, f.Log, clock);
        var worker = Create(); await worker.InitializeAsync(default);
        await worker.ConfigureAsync((await worker.StatusAsync(default)).Configuration with { Sources = [source], SummaryModel = "model-a", AiPaused = true }, default);
        await Fetch(worker, source); Check((await worker.StatusAsync(default)).PendingArticles == 2 && (await f.Store.QueryAsync(new())).TotalUnread == 1, "paused AI still collects but pending items are not notifications");
        worker = Create(); await worker.InitializeAsync(default);
        await worker.SetAiPausedAsync(false, default); chat.EnqueueComplete("要約1"); chat.EnqueueComplete("要約2"); await worker.TickAiAsync(default); await worker.TickAiAsync(default);
        var page = await f.Store.QueryAsync(new());
        Check(page.Items.Count == 3 && page.Items.Any(item => item.Notification.Id == oldId), "old RSS article and notification identity preserved");
        var first = page.Items.Single(item => item.GitHubRelease?.ReleaseId == 1); var acquired = first.FetchedAt;
        await f.Store.SetReadAsync(new(first.Notification.Id, true)); await f.Store.SetBookmarkAsync(new(first.Notification.Id, true)); await f.Store.AddTagAsync(new(first.Notification.Id, "調査"));
        edited = true; await Fetch(worker, source);
        var after = (await f.Store.QueryAsync(new(Filter: NotificationViewFilter.Bookmarked))).Items.Single();
        Check(after.Notification.Id == first.Notification.Id && after.FetchedAt == acquired && after.ReadAt is not null && after.Tags!.Count == 1 && after.GitHubRelease!.TagName == "v1" && chat.Requests.Count == 2, "same ID edit does not re-notify/re-summarize or reset metadata");
        Check((await f.Store.PendingAsync(source.Url, default)).Count == 0 && chat.Requests[0].Messages[0].Content.Contains("GitHubRelease"), "metadata reaches independent summary input");
        var deleted = await worker.DeleteAsync([first.Notification.Id], default); Check(deleted.DeletedIds.Count == 1, "shared delete service");
        await Fetch(worker, source); chat.EnqueueComplete("再取得要約"); await worker.TickAiAsync(default);
        var fresh = (await f.Store.QueryAsync(new())).Items.Single(item => item.GitHubRelease?.ReleaseId == 1);
        Check(fresh.Notification.Id != first.Notification.Id && fresh.ReadAt is null && !fresh.IsBookmarked && fresh.Tags!.Count == 0 && (await f.Store.RunsAsync(default)).Count == 4, "recollect new identity and default metadata, preserve old run and three new runs");
        Check(File.ReadAllText(Path.Combine(f.Paths.LocalDirectory, fresh.MarkdownPath)).Contains("リリースタグ: v1"), "saved release metadata");
    }
    public static async Task SharedRateLimitAndRecovery()
    {
        using var f = new NotificationManagementTests.Fixture(); var clock = new Clock(); var requests = 0; var fail = true;
        using var client = Client((_, _) => { requests++; if (!fail) return Task.FromResult(Json([])); var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests); r.Headers.RetryAfter = new(TimeSpan.FromHours(2)); return Task.FromResult(r); });
        var rss = new RssStub(); var settings = new WorkerStateStore(f.Paths, f.Store, new JsonUserSettingsStore(f.Paths, f.Log));
        WorkerCoordinator Create() => new(settings, f.Store, new ContentSourceRouter(rss, new GitHubReleasesSource(client, clock)), new ArticleMarkdownWriter(f.Paths), new TestChatService(), new TestModelControl(), FileOperationGate.ForModels(f.Paths), f.Log, f.Log, clock);
        var worker = Create(); await worker.InitializeAsync(default);
        var one = Source() with { Automatic = true }; var two = Source("other/project") with { Automatic = true }; var feed = new SourceConfiguration(Guid.NewGuid(), "RSS", "https://example.com/feed");
        await worker.ConfigureAsync((await worker.StatusAsync(default)).Configuration with { Sources = [one, two, feed], AiPaused = true }, default);
        await Fetch(worker, one);
        Check(requests == 1 && (await worker.StatusAsync(default)).Sources.Where(item => item.Id != feed.Id).All(item => item.NextDueAt >= clock.GetUtcNow().AddHours(2)), "shared wait is visible for every GitHub source");
        await Expect<WorkerException>(() => worker.RequestFetchAsync(new(two.Id, Guid.NewGuid()), default));
        await Fetch(worker, feed); Check(rss.Calls == 1, "RSS continues while GitHub is limited");
        worker = Create(); await worker.InitializeAsync(default);
        await worker.ConfigureAsync((await worker.StatusAsync(default)).Configuration with { Sources = [two, feed] }, default);
        await Expect<WorkerException>(() => worker.RequestFetchAsync(new(two.Id, Guid.NewGuid()), default));
        Check(requests == 1, "restart and deleting limited source cannot bypass shared cooldown");
        fail = false; clock.Advance(TimeSpan.FromHours(3)); await Fetch(worker, two);
        Check(requests == 2 && (await f.Store.RunsAsync(default)).Any(run => run.Status == "レート制限待機") && (await f.Store.RunsAsync(default)).Any(run => run.Status == "取得完了（要約は別処理）"), "cooldown recovery is distinct from update-none failure");
    }
    public static async Task LegacyConfigurationAndValidation()
    {
        using var f = new NotificationManagementTests.Fixture(); Directory.CreateDirectory(f.Paths.SettingsDirectory);
        var id = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(f.Paths.SettingsDirectory, "worker-settings.json"), JsonSerializer.Serialize(new { version = 4, summaryModel = "model-a", allowLoad = false, allowUnload = false, aiPaused = false, sources = new[] { new { id, name = "旧 RSS", url = "https://example.com/feed", intervalMinutes = 60, automatic = true, deleted = false } } }));
        var settings = new WorkerStateStore(f.Paths, f.Store, new JsonUserSettingsStore(f.Paths, f.Log)); var old = WorkerCoordinator.Validate(await settings.LoadConfigurationAsync(default));
        Check(old.Sources.Single().Id == id && old.Sources[0].Kind == ContentSourceKind.RssAtom && !old.Sources[0].IncludePrereleases, "old settings retain RSS source ID and safe defaults");
        var config = WorkerCoordinator.Validate(old with { Sources = old.Sources.Append(Source("Microsoft/VSCode") with { IncludePrereleases = true }).ToArray() });
        await settings.SaveConfigurationAsync(config, default);
        var loaded = await settings.LoadConfigurationAsync(default);
        Check(loaded.Sources.Last().Kind == ContentSourceKind.GitHubReleases && loaded.Sources.Last().IncludePrereleases && loaded.Sources[0].Id == id, "kind and prerelease options survive restart");
        await Expect<WebMonitoringException>(() => Task.FromResult(WorkerCoordinator.Validate(old with { Sources = [Source() with { Kind = (ContentSourceKind)99 }] })));
    }
    public static async Task SourceEditorAndApiErrors()
    {
        using var f = new NotificationManagementTests.Fixture();
        var files = new WorkerConnectionFiles(f.Paths);
        await files.PublishAsync(new(WorkerCoordinator.Protocol, WorkerCoordinator.ImplementationVersion, "http://127.0.0.1:12345/", new string('a', 64), Guid.NewGuid()), default);
        var configuration = WorkerConfiguration.Default; string? failure = null; TaskCompletionSource? saved = null;
        using var http = Client(async (request, token) =>
        {
            if (failure is not null) return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(failure) };
            if (request.Method == HttpMethod.Put)
            {
                var next = JsonSerializer.Deserialize(await request.Content!.ReadAsStringAsync(token), WorkerJsonContext.Default.WorkerConfiguration)!;
                if (saved is not null) await saved.Task;
                configuration = WorkerCoordinator.Validate(next) with { Version = configuration.Version + 1 };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(configuration, WorkerJsonContext.Default.WorkerConfiguration)) };
            }
            var status = new WorkerStatus(1, WorkerCoordinator.ImplementationVersion, Guid.Empty, DateTimeOffset.UtcNow, configuration, [], 0, "待機中", "", false, false);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(status, WorkerJsonContext.Default.WorkerStatus)) };
        });
        var client = new WorkerApiClient(http, files);
        using var vm = new WorkerViewModel(client, new NoLauncher(), new TestChatService(), f.Log);
        await vm.InitializeAsync(); vm.KindIndex = 1; vm.Name = "GitHub"; vm.Url = "Microsoft/VSCode"; vm.IncludePrereleases = true;
        saved = new TaskCompletionSource(); var saving = vm.SaveSourceCommand.ExecuteAsync();
        vm.Url = "other/project"; vm.IncludePrereleases = false; saved.SetResult(); await saving; saved = null;
        Check(vm.Url == "other/project" && !vm.IncludePrereleases && vm.HasSourceEdits && configuration.Sources.Single().IncludePrereleases, "newer repository/option draft survives old save result");
        vm.SelectedSource = null; vm.SelectedSource = vm.Sources.Single();
        Check(vm.IsGitHubSource && vm.Url == "microsoft/vscode" && vm.IncludePrereleases && !vm.HasSourceEdits, "selection restores kind, editable repository and saved option");
        await vm.PollAsync(); Check(vm.IsGitHubSource && vm.IncludePrereleases, "poll does not reset source kind");
        failure = "GitHub API の取得を待機中です。解除予定以降に実行してください。";
        var error = await Expect<WorkerException>(() => client.FetchAsync(new(Guid.NewGuid(), Guid.NewGuid())));
        Check(error.Message == failure && !error.ConnectionFailure, "safe API reason remains visible without fake disconnect");
        failure = new string('x', 3000);
        Check((await Expect<WorkerException>(() => client.FetchAsync(new(Guid.NewGuid(), Guid.NewGuid())))).Message.Length < 2048, "oversized API error is bounded");
    }
    private static SourceConfiguration Source(string repository = "owner/project") => new(Guid.NewGuid(), "GitHub", ContentSourceIdentity.Normalize(ContentSourceKind.GitHubReleases, repository), Kind: ContentSourceKind.GitHubReleases);
    private static async Task Fetch(WorkerCoordinator worker, SourceConfiguration source) { await worker.RequestFetchAsync(new(source.Id, Guid.NewGuid()), default); await worker.TickFetchAsync(default); }
    private static object Release(int id, bool pre = false, bool draft = false, string? title = "リリース", string? body = "本文") => new { id, name = title, body, html_url = "https://github.com/owner/project/releases/tag/v" + id, tag_name = "v" + id, published_at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(id), prerelease = pre, draft };
    private static HttpResponseMessage Json(object[] values) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(values), Encoding.UTF8, "application/json") };
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => new(new StubHandler(handler)) { Timeout = Timeout.InfiniteTimeSpan };
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task<T> Expect<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T exception) { return exception; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private sealed class RssStub : IFeedSource
    { public int Calls; public Task<IReadOnlyList<FeedArticle>> FetchAsync(string url, CancellationToken cancellationToken) { Calls++; return Task.FromResult<IReadOnlyList<FeedArticle>>([]); } }
    private sealed class Clock : TimeProvider
    { private DateTimeOffset _now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()); public override DateTimeOffset GetUtcNow() => _now; public void Advance(TimeSpan value) => _now += value; }
    private sealed class NoLauncher : IWorkerLauncher
    {
        public Task StartAsync() => Task.CompletedTask;
        public Task<WorkerStartupState> GetStartupAsync() => Task.FromResult(new WorkerStartupState(false, "検証用"));
        public Task<WorkerStartupState> SetStartupAsync(bool enabled) => GetStartupAsync();
    }
}
