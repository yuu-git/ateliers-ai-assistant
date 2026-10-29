using System.Runtime.CompilerServices;
using System.Text.Json;
using Ateliers.Ai.Assistant.Application.Chat;
using Ateliers.Ai.Assistant.Application.Logging;
using Ateliers.Ai.Assistant.Application.Models;
using Ateliers.Ai.Assistant.Application.Settings;
using Ateliers.Ai.Assistant.Application.Storage;
using Ateliers.Ai.Assistant.Application.WebMonitoring;
using Ateliers.Ai.Assistant.Application.Worker;
using Ateliers.Ai.Assistant.Core.Chat;
using Ateliers.Ai.Assistant.Desktop.ViewModels;
using Ateliers.Ai.Assistant.Infrastructure.Storage;
using Ateliers.Ai.Assistant.Infrastructure.WebMonitoring;
using Ateliers.Ai.Assistant.Infrastructure.Worker;

namespace Ateliers.Ai.Assistant.Desktop.Tests;

/// <summary>実 DB とファイル、偽時計・RSS・Ollama で常駐の資源制御と復旧を検証する。</summary>
internal static class WorkerTests
{
    /// <summary>即時一時停止は保存済みモデル・ロード方針・巡回予定を変更しない。</summary>
    public static async Task PauseOnlyConfiguration()
    {
        using var f = new Fixture(); var worker = f.Worker(); await f.Setup(worker, automatic: true);
        var before = await worker.StatusAsync(default);
        var saved = await worker.SetAiPausedAsync(true, default);
        var after = await worker.StatusAsync(default);
        Check(saved.AiPaused && saved.SummaryModel == before.Configuration.SummaryModel && saved.AllowLoad == before.Configuration.AllowLoad && saved.AllowUnload == before.Configuration.AllowUnload && saved.Sources.SequenceEqual(before.Configuration.Sources), "pause changes no other settings");
        Check(after.Sources.Single().NextDueAt == before.Sources.Single().NextDueAt, "pause does not change RSS schedule");
    }
    /// <summary>AI 一時停止中にも取得を保存し、最新10件から外れた待機記事を後で処理する。</summary>
    public static async Task PauseFetchAndBacklog()
    {
        using var f = new Fixture(); var worker = f.Worker();
        await f.Setup(worker, paused: true); await f.Fetch(worker);
        await worker.TickAiAsync(default);
        Check((await worker.StatusAsync(default)).PendingArticles == 10 && f.Adapter.Replies == 0 && f.Adapter.Loads == 0, "pause preserves RSS and prevents all model loads");
        f.Adapter.Batch = 1; await f.Fetch(worker);
        Check((await worker.StatusAsync(default)).PendingArticles == 20, "new feed entries persisted while old entries leave feed");
        var status = await worker.StatusAsync(default); await worker.ConfigureAsync(status.Configuration with { AiPaused = false }, default);
        for (var i = 0; i < 20; i++) await worker.TickAiAsync(default);
        Check((await worker.StatusAsync(default)).PendingArticles == 0 && f.Adapter.Replies == 20 && (await f.Store.NotificationsAsync(default)).Count == 20, "resume drains durable backlog including entries outside current feed");
    }
    /// <summary>ロード OFF は暗黙ロードを防ぎ、アンロード ON でも既存モデルは停止しない。</summary>
    public static async Task ModelPolicies()
    {
        using var f = new Fixture(); f.Adapter.Count = 1; var worker = f.Worker(); await f.Setup(worker, load: false, unload: true);
        await f.Fetch(worker); await worker.TickAiAsync(default);
        Check(f.Adapter.Loads == 0 && f.Adapter.Replies == 0 && (await worker.StatusAsync(default)).AiState.Contains("未ロード"), "OFF never sends chat to unloaded model");
        f.Adapter.Loaded = true; f.Clock.Advance(TimeSpan.FromSeconds(6)); await worker.TickAiAsync(default);
        Check(f.Adapter.Replies == 1 && f.Adapter.Unloads == 0, "preloaded model used but never unloaded");
        using var own = new Fixture(); own.Adapter.Count = 1; var ownWorker = own.Worker(); await own.Setup(ownWorker, load: true, unload: true);
        await own.Fetch(ownWorker); await ownWorker.TickAiAsync(default);
        Check(own.Adapter.Loads == 1 && own.Adapter.Unloads == 1 && own.Adapter.Replies == 1, "only newly loaded model explicitly released");
        using var keep = new Fixture(); keep.Adapter.Count = 1; var keepWorker = keep.Worker(); await keep.Setup(keepWorker, load: true, unload: false);
        await keep.Fetch(keepWorker); await keepWorker.TickAiAsync(default);
        Check(keep.Adapter.Unloads == 0 && keep.Adapter.Loaded, "unload OFF never sends explicit release");
    }
    /// <summary>一時停止は生成を中止し、設定と未完了段階が再起動後も残る。</summary>
    public static async Task CancelAndRestart()
    {
        using var f = new Fixture(); f.Adapter.Count = 1; f.Adapter.Slow = true;
        var worker = f.Worker(); await f.Setup(worker); await f.Fetch(worker);
        var generating = worker.TickAiAsync(default); await f.Adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var status = await worker.StatusAsync(default); await worker.ConfigureAsync(status.Configuration with { AiPaused = true }, default);
        await generating.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!(await f.Store.PendingAsync(f.Source.Url, default)).Single().Processing && (await f.Store.NotificationsAsync(default)).Count == 0, "cancel returns to queue without notification");
        var restarted = f.Worker(); await restarted.InitializeAsync(default); await restarted.TickAiAsync(default);
        Check((await restarted.StatusAsync(default)).Configuration.AiPaused && f.Adapter.Replies == 1, "pause persisted through restart");
        f.Adapter.Slow = false; var next = await restarted.StatusAsync(default);
        await restarted.ConfigureAsync(next.Configuration with { AiPaused = false }, default); await restarted.TickAiAsync(default);
        Check((await f.Store.NotificationsAsync(default)).Count == 1, "resumed incomplete article creates exactly one notification");
    }
    /// <summary>期間超過は1回だけ追いつき、周期変更は再起動せず次回予定を更新する。</summary>
    public static async Task ScheduleAndRequests()
    {
        using var f = new Fixture(); var worker = f.Worker(); await f.Setup(worker, automatic: true);
        f.Clock.Advance(TimeSpan.FromHours(2)); await worker.TickFetchAsync(default); await worker.TickFetchAsync(default);
        Check(f.Adapter.Fetches == 1 && (await worker.StatusAsync(default)).Sources.Single().NextDueAt > f.Clock.GetUtcNow(), "one catch-up, not all missed periods");
        var configuration = (await worker.StatusAsync(default)).Configuration;
        await worker.ConfigureAsync(configuration with { Sources = [f.Source with { IntervalMinutes = 17, Automatic = true }] }, default);
        Check((await worker.StatusAsync(default)).Sources.Single().NextDueAt == f.Clock.GetUtcNow().AddMinutes(17), "interval applies immediately");
        configuration = (await worker.StatusAsync(default)).Configuration;
        await worker.ConfigureAsync(configuration with { Sources = [f.Source with { Automatic = false }] }, default);
        var request = new FetchRequest(f.Source.Id, Guid.NewGuid());
        var accepted = await worker.RequestFetchAsync(request, default);
        Check((await worker.RequestFetchAsync(new(f.Source.Id, Guid.NewGuid()), default)).RunId == accepted.RunId, "same source not queued twice");
        await worker.TickFetchAsync(default); var count = f.Adapter.Fetches;
        var restarted = f.Worker(); await restarted.InitializeAsync(default);
        Check((await restarted.RequestFetchAsync(request, default)).AlreadyRunning, "request idempotency persists through process restart");
        await restarted.TickFetchAsync(default); Check(f.Adapter.Fetches == count, "completed request is not executed again");
        await restarted.RequestFetchAsync(new(f.Source.Id, Guid.NewGuid()), default);
        using (var databaseLock = new FileStream(f.Paths.DatabaseFile + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            await Expect<StorageException>(() => restarted.TickFetchAsync(default));
        await restarted.TickFetchAsync(default);
        Check(f.Adapter.Fetches == count + 1 && (await restarted.StatusAsync(default)).Sources.Single().ActiveRunId is null,
            "temporary database contention does not lose the accepted request");
    }
    /// <summary>処理中フラグを復旧し、保存済み要約をファイル失敗後に再利用する。</summary>
    public static async Task QueueRecoveryAndFiles()
    {
        using var f = new Fixture(); f.Adapter.Count = 1;
        var worker = f.Worker(); await f.Setup(worker); await f.Fetch(worker);
        var pending = (await f.Store.PendingAsync(f.Source.Url, default)).Single(); await f.Store.TryBeginAsync(pending.Id, default);
        var restarted = f.Worker(); await restarted.InitializeAsync(default);
        Check(!(await f.Store.PendingAsync(f.Source.Url, default)).Single().Processing, "interrupted article recovered");
        f.Writer.Fail = true; await restarted.TickAiAsync(default);
        Check((await f.Store.PendingAsync(f.Source.Url, default)).Single().Summary is not null && (await f.Store.NotificationsAsync(default)).Count == 0, "summary persisted separately from file");
        f.Writer.Fail = false; f.Clock.Advance(TimeSpan.FromMinutes(2)); await restarted.TickAiAsync(default);
        Check(f.Adapter.Replies == 1 && (await f.Store.NotificationsAsync(default)).Count == 1, "file retry avoids duplicate summary/notification");
    }
    /// <summary>情報源削除で記事を保持し、再登録時も重複を継承する。記事削除は新規再取得できる。</summary>
    public static async Task SourceAndArticleDeletion()
    {
        using var f = new Fixture(); f.Adapter.Count = 1; var worker = f.Worker(); await f.Setup(worker); await f.Fetch(worker); await worker.TickAiAsync(default);
        var config = (await worker.StatusAsync(default)).Configuration;
        await worker.ConfigureAsync(config with { Sources = [f.Source with { Deleted = true }] }, default);
        Check((await f.Store.NotificationsAsync(default)).Count == 1 && (await worker.StatusAsync(default)).PendingArticles == 0, "source removal preserves articles");
        config = (await worker.StatusAsync(default)).Configuration;
        await worker.ConfigureAsync(config with { Sources = [f.Source] }, default); await f.Fetch(worker); await worker.TickAiAsync(default);
        Check(f.Adapter.Replies == 1, "source re-registration retains deduplication");
        var id = (await f.Store.NotificationsAsync(default)).Single().Notification.Id;
        await worker.DeleteAsync([id], default); await f.Fetch(worker); await worker.TickAiAsync(default);
        Check(f.Adapter.Replies == 2 && (await f.Store.NotificationsAsync(default)).Single().Notification.Id != id, "article deletion permits new collection");
    }
    /// <summary>旧設定を自動 OFF で1回だけ取り込み、競合・不正設定は上書きしない。</summary>
    public static async Task SettingsMigrationAndValidation()
    {
        using var f = new Fixture();
        await new JsonUserSettingsStore(f.Paths, f.Log).SaveAsync(new("chat-model", f.Source.Url, "worker-model"));
        var initial = await f.Settings.LoadConfigurationAsync(default);
        Check(initial.Sources.Count == 1 && !initial.Sources[0].Automatic && !initial.AllowLoad && !initial.AllowUnload && initial.SummaryModel == "worker-model", "safe legacy import");
        var saved = await f.Settings.SaveConfigurationAsync(initial, default);
        await Expect<WorkerException>(() => f.Settings.SaveConfigurationAsync(initial, default));
        Check((await f.Settings.LoadConfigurationAsync(default)).Sources[0].Id == initial.Sources[0].Id, "import only once");
        foreach (var minutes in new[] { 0, 4, 10081 })
            ExpectSync<WorkerException>(() => WorkerCoordinator.Validate(saved with { Sources = [f.Source with { IntervalMinutes = minutes }] }));
        ExpectSync<WebMonitoringException>(() => WorkerCoordinator.Validate(saved with { Sources = [f.Source with { Url = "http://127.0.0.1/private" }] }));
        File.WriteAllText(Path.Combine(f.Paths.SettingsDirectory, "worker-settings.json"), "{broken");
        await Expect<StorageException>(() => f.Settings.LoadConfigurationAsync(default));
        Check(File.ReadAllText(Path.Combine(f.Paths.SettingsDirectory, "worker-settings.json")) == "{broken", "invalid worker config protected");
    }
    /// <summary>モデルゲートで UI の停止・終了が Worker の実行中モデルを解放しない。</summary>
    public static async Task CrossProcessModelGate()
    {
        using var f = new Fixture(); f.Adapter.Count = 1;
        var gate = FileOperationGate.ForModels(f.Paths);
        var ui = new ChatModelSession(f.Adapter, f.Adapter, gate);
        await ui.LoadAsync("model");
        var worker = f.Worker(); await f.Setup(worker, load: false); await f.Fetch(worker); f.Adapter.Slow = true;
        var generation = worker.TickAiAsync(default); await f.Adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Expect<ChatServiceException>(() => ui.UnloadAsync("model"));
        await ui.ShutdownAsync(TimeSpan.FromMilliseconds(100));
        Check(f.Adapter.Unloads == 0, "UI shutdown does not unload active Worker model");
        worker.CancelAi(); await generation;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1)); using var released = await gate.EnterAsync(timeout.Token);
    }
    /// <summary>分類・期間・レベル・キーワード・上限、旧形式、コピーを Worker 不在でも扱う。</summary>
    public static async Task LogsOfflineAndPrivacy()
    {
        using var f = new Fixture();
        var ui = new FileDiagnosticLog(f.Paths, "UI");
        for (var i = 0; i < 30; i++) ui.Write(AppLogCategory.Application, AppLogLevel.Information, "test.run", "巡回完了 " + i);
        ui.Write(AppLogCategory.Application, AppLogLevel.Warning, "test.warning", "対象の警告");
        f.Log.Error("test.exception", new IOException("SECRET Bearer token and prompt"));
        var reader = new FileAppLogReader(f.Paths);
        Check((await reader.ReadAsync(new(AppLogCategory.Application, Limit: 5))).Count == 5, "bounded reader");
        Check((await reader.ReadAsync(new(AppLogCategory.Application, AppLogLevel.Warning, Keyword: "対象"))).Count == 1, "level and keyword filters");
        Check((await reader.ReadAsync(new(AppLogCategory.Application, Since: DateTimeOffset.UtcNow.AddHours(1)))).Count == 0, "period filter");
        var system = await reader.ReadAsync(new(AppLogCategory.System));
        Check(system.Count == 1 && !system[0].Message.Contains("SECRET") && system[0].Origin == "Worker", "exception messages and secrets excluded");
        var copy = ""; var opened = false;
        var vm = new LogsViewModel(reader, text => { copy = text; return Task.CompletedTask; }, () => { opened = true; return Task.CompletedTask; });
        await vm.RefreshAsync(); vm.Select(vm.Entries.Take(2).ToArray()); await vm.CopySelectedCommand.ExecuteAsync();
        Check(copy.Contains("[UI]") && copy.Contains("操作:") && copy.Contains("分類:"), "explicit multi-copy has readable metadata");
        await vm.CopyDetailsCommand.ExecuteAsync(); await vm.OpenFolderCommand.ExecuteAsync(); Check(opened, "fixed log folder operation");
        // 実ファイルのローテーションと並行読込を検証する。実ユーザーのログは使わない。
        var bulkWriter = Task.Run(() =>
        {
            var secondUi = new FileDiagnosticLog(f.Paths, "UI");
            for (var i = 0; i < 2200; i++) secondUi.Write(AppLogCategory.Application, AppLogLevel.Information, "rotation.bulk", new string('x', 2048));
        });
        var concurrentReader = Task.Run(async () =>
        {
            for (var i = 0; i < 6; i++)
            {
                Check((await reader.ReadAsync(new(AppLogCategory.Application))).Count <= 500, "concurrent read remains bounded");
                await Task.Delay(10);
            }
        });
        await Task.WhenAll(bulkWriter, concurrentReader);
        var rotated = Directory.GetFiles(f.Paths.LogsDirectory, "ui*.log");
        Check(rotated.Length == 3 && rotated.All(file => new FileInfo(file).Length <= FileDiagnosticLog.MaximumFileBytes), "structured logs rotate within three bounded files");
        await File.AppendAllTextAsync(Path.Combine(f.Paths.LogsDirectory, "ui.log"), "{\"partial\":");
        Check((await reader.ReadAsync(new(AppLogCategory.Application, Keyword: "rotation.bulk"))).Count == 500, "partial line ignored and latest 500 retained");
        using var files = new WorkerConnectionFiles(f.Paths).TryEnterInstance();
        Check(new WorkerConnectionFiles(f.Paths).TryEnterInstance() is null, "single instance gate");
        var release = new AppDataPaths(Path.Combine(f.Root, "r"), Path.Combine(f.Root, "l"), AppDataEnvironment.Release);
        var debug = new AppDataPaths(Path.Combine(f.Root, "r"), Path.Combine(f.Root, "l"), AppDataEnvironment.Debug);
        using var releaseLock = new WorkerConnectionFiles(release).TryEnterInstance();
        if (f.Paths.DataEnvironment == AppDataEnvironment.Debug) Check(releaseLock is not null, "environment-separated Worker instances");
    }
    private static void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); }
    private static async Task Expect<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void ExpectSync<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ateliers-worker-tests-" + Guid.NewGuid().ToString("N"));
        public AppDataPaths Paths { get; }
        public FileDiagnosticLog Log { get; }
        public SqliteWebMonitoringStore Store { get; }
        public WorkerStateStore Settings { get; }
        public Adapter Adapter { get; } = new();
        public Clock Clock { get; } = new();
        public Writer Writer { get; }
        public SourceConfiguration Source { get; } = new(Guid.NewGuid(), "テスト情報源", "https://example.com/worker", 5);
        public Fixture()
        {
            Paths = new(Path.Combine(Root, "r"), Path.Combine(Root, "l")); Log = new(Paths, "Worker"); Store = new(Paths, Log);
            Settings = new(Paths, Store, new JsonUserSettingsStore(Paths, Log)); Writer = new(new ArticleMarkdownWriter(Paths));
        }
        public WorkerCoordinator Worker() => new(Settings, Store, Adapter, Writer, Adapter, Adapter, FileOperationGate.ForModels(Paths), Log, Log, Clock);
        public async Task Setup(WorkerCoordinator worker, bool paused = false, bool load = true, bool unload = false, bool automatic = false)
        {
            await worker.InitializeAsync(default);
            var config = (await worker.StatusAsync(default)).Configuration;
            await worker.ConfigureAsync(config with { SummaryModel = "model", AiPaused = paused, AllowLoad = load, AllowUnload = unload, Sources = [Source with { Automatic = automatic }] }, default);
        }
        public async Task Fetch(WorkerCoordinator worker) { await worker.RequestFetchAsync(new(Source.Id, Guid.NewGuid()), default); await worker.TickFetchAsync(default); }
        public void Dispose()
        {
            var full = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(full) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(full).StartsWith("ateliers-worker-tests-", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe temporary root");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
        public override DateTimeOffset GetUtcNow() => _now;
    }
    private sealed class Writer(IArticleMarkdownWriter inner) : IArticleMarkdownWriter
    {
        public bool Fail;
        public Task<string> WriteAsync(CollectedArticle article, CancellationToken token) => Fail ? throw new StorageException("検証用の書込失敗") : inner.WriteAsync(article, token);
    }
    private sealed class Adapter : IFeedSource, IChatService, IModelControlService
    {
        public int Count = 12, Batch, Fetches, Replies, Loads, Unloads;
        public bool Slow, Loaded;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<FeedArticle>> FetchAsync(string url, CancellationToken cancellationToken)
        {
            Fetches++;
            return Task.FromResult<IReadOnlyList<FeedArticle>>(Enumerable.Range(0, Count).Select(index => new FeedArticle(FeedIdentity.Key(url, $"{Batch}-{index}", null, "", null),
                url, $"{Batch}-{index}", "https://example.com/article", "テスト記事 " + index, DateTimeOffset.UnixEpoch.AddDays(Batch * 100 + index), "外部概要", "概要のみ")).ToArray());
        }
        public Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(["model"]);
        public async IAsyncEnumerable<string> StreamReplyAsync(string model, IReadOnlyList<ChatMessage> messages, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { Replies++; Started.TrySetResult(); if (Slow) await Task.Delay(Timeout.Infinite, cancellationToken); yield return "# 要約\n\n日本語の要約です。"; }
        public Task<IReadOnlyList<LoadedModel>> GetLoadedModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LoadedModel>>(Loaded ? [new("model", "digest", 1, 1, "Q4", null)] : []);
        public Task LoadModelAsync(string model, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); Loaded = true; Loads++; return Task.CompletedTask; }
        public Task UnloadModelAsync(string model, CancellationToken cancellationToken = default) { Loaded = false; Unloads++; return Task.CompletedTask; }
    }
}
