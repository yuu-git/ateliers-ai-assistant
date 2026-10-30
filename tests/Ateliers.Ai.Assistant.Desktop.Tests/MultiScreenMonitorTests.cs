using System.Runtime.CompilerServices;
using System.Text.Json;
using Ateliers.Ai.Assistant.Application.Chat;
using Ateliers.Ai.Assistant.Application.Models;
using Ateliers.Ai.Assistant.Application.ScreenAnalysis;
using Ateliers.Ai.Assistant.Application.Storage;
using Ateliers.Ai.Assistant.Application.WebMonitoring;
using Ateliers.Ai.Assistant.Application.Worker;
using Ateliers.Ai.Assistant.Core.Chat;
using Ateliers.Ai.Assistant.Infrastructure.Storage;
using Ateliers.Ai.Assistant.Infrastructure.Conversations;
using Ateliers.Ai.Assistant.Infrastructure.WebMonitoring;
using Ateliers.Ai.Assistant.Infrastructure.Worker;
using Ateliers.Ai.Assistant.Desktop.ViewModels;

namespace Ateliers.Ai.Assistant.Desktop.Tests;

internal static class MultiScreenMonitorTests
{
    public static async Task ModelPolicyAndFailure()
    {
        await using var f = await Fixture.Create(); var a = await f.Add("A", "unloaded"); var b = await f.Add("B", "vision");
        await f.Worker.ConfigureAsync(f.Worker.Configuration with { AllowLoad = false }, default);
        await f.Manager.StartAsync(a, default); await f.Manager.StartAsync(b, default); await f.RunOne();
        Check(f.Captures.Sum(c => c.Count) == 0 && f.Port.Requests.Count == 0, "automatic load OFF never captures or implicitly loads");
        await f.Port.LoadModelAsync("vision");
        for (var i = 0; i < 3 && f.Manager.Detail(b).Comments.Count == 0; i++) { f.Clock.Advance(31); await f.RunOne(); }
        Check(f.Manager.Detail(b).Comments.Count == 1 && !f.Worker.Configuration.AllowLoad, "preloaded other model works without altering settings");
        await f.Worker.ConfigureAsync(f.Worker.Configuration with { AllowLoad = true }, default);
        f.Port.FailModel = "bad"; var c = await f.Add("failure", "bad"); await f.Manager.StartAsync(c, default); await f.RunOne();
        Check(!f.Manager.List().Monitors.Single(m => m.Definition.MonitorId == c).Active && f.Manager.Detail(c).Comments.Single().Status == "失敗", "unsupported model preserves partial failed comment");
        Check(f.Manager.List().Monitors.Single(m => m.Definition.MonitorId == b).Active, "one model's failure never cancels the other monitor");
    }
    public static async Task TargetAndPickerIsolation()
    {
        await using var f = await Fixture.Create(); var a = await f.Add("A"); var b = await f.Add("B");
        await f.Manager.StartAsync(a, default); await f.Manager.StartAsync(b, default); await f.RunOne();
        f.Captures[0].Close();
        Check(!f.Manager.List().Monitors.Single(m => m.Definition.MonitorId == a).Active && f.Manager.List().Monitors.Single(m => m.Definition.MonitorId == b).Active, "target closed notification affects only its monitor");
        f.Clock.Advance(31); await f.RunOne(); Check(f.Manager.Detail(b).Comments.Count > 0, "remaining target keeps working");
        f.Captures[0].Selection = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await f.Manager.PickAsync(a, default); await Expect<WorkerException>(() => f.Manager.DeleteAsync(a, default));
        f.Manager.Stop(a); Check(f.Manager.List().Monitors.Single(m => m.Definition.MonitorId == a).Picking, "cancel does not falsely release OS picker ownership");
        f.Captures[0].Selection!.SetResult(); await Until(() => !f.Manager.List().Monitors.Single(m => m.Definition.MonitorId == a).Picking);
        Check(f.Manager.List().Monitors.Single(m => m.Definition.MonitorId == b).Active, "selection cancellation never stops another session");
    }
    public static async Task FairnessAndIsolation()
    {
        await using var f = await Fixture.Create();
        var a = await f.Add("A", "vision", 10, "A の指示"); var b = await f.Add("B", "other", 30, "B の指示");
        await f.Manager.StartAsync(a, default); await f.Manager.StartAsync(b, default);
        for (var i = 0; i < 8; i++) { f.Clock.Advance(31); await f.RunOne(); }
        Check(f.Port.Maximum == 1 && f.Port.Requests.Any(r => r.Prompt == "A の指示" && r.Model == "vision") && f.Port.Requests.Any(r => r.Prompt == "B の指示" && r.Model == "other"), "independent model/prompt and serial inference");
        Check(f.Manager.Detail(a).Comments.Count >= 3 && f.Manager.Detail(b).Comments.Count >= 3, "different periods cannot starve a monitor");
        var bCount = f.Manager.Detail(b).Comments.Count;
        f.Manager.Stop(a); await f.Manager.DeleteAsync(a, default); f.Clock.Advance(31); await f.RunOne();
        Check(f.Manager.List().Monitors.Single().Definition.MonitorId == b && f.Manager.Detail(b).Comments.Count > bCount, "deleting A preserves B target and comments");
        await f.Add("C"); await f.Add("D"); await f.Add("E"); await Expect<WorkerException>(() => f.Manager.CreateAsync("over limit", default));
        Check(f.Manager.List().Monitors.Count == 4, "registration limit includes stopped definitions");
    }
    public static async Task CancellationRetainsSlotAndModel()
    {
        await using var f = await Fixture.Create();
        var a = await f.Add("A"); var b = await f.Add("B", "vision:latest"); f.Port.IgnoreCancellation = true; f.Port.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await f.Manager.StartAsync(a, default); await f.Manager.StartAsync(b, default);
        await f.Manager.TickAsync(default); await Until(() => f.Port.Requests.Count == 1);
        var running = f.Manager.List().Monitors.Single(m => m.Processing).Definition.MonitorId;
        var other = running == a ? b : a;
        f.Manager.Stop(running); await f.Manager.DeleteAsync(running, default);
        for (var i = 0; i < 5; i++) { f.Clock.Advance(31); await f.Manager.TickAsync(default); }
        Check(f.Port.Requests.Count == 1 && f.Manager.List().Monitors.Single(m => m.Definition.MonitorId == running).Processing, "cancel request never releases actual inference slot");
        Check(f.Manager.ProtectsModel("vision") && f.Port.Unloads.Count == 0, "other monitor protects shared model while deletion waits");
        f.Port.IgnoreCancellation = false; f.Port.Release.SetResult(); await Until(() => !f.Manager.List().Monitors.Any(m => m.Processing));
        await f.RunOne();
        Check(f.Manager.List().Monitors.Single().Definition.MonitorId == other && f.Manager.Detail(other).Comments.Count == 1 && f.Port.Maximum == 1, "actual completion releases lease then deletes and advances");
        f.Manager.Stop(other); await f.Manager.TickAsync(default);
        Check(f.Port.Unloads.Count == 1 && ChatModelSession.SameModel(f.Port.Unloads[0], "vision"), "last monitor releases shared model exactly once including default tag aliases");
    }
    public static async Task PauseTimeoutAndQuota()
    {
        await using var f = await Fixture.Create(); var a = await f.Add("A"); var b = await f.Add("B");
        await f.Worker.SetAiPausedAsync(true, default); await Expect<WorkerException>(() => f.Manager.StartAsync(a, default));
        await f.Worker.SetAiPausedAsync(false, default);
        f.Port.IgnoreCancellation = true; f.Port.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await f.Manager.StartAsync(a, default); await f.Manager.StartAsync(b, default); await f.Manager.TickAsync(default); await Until(() => f.Port.Requests.Count == 1);
        await f.Worker.SetAiPausedAsync(true, default); f.Clock.Advance(700); await f.Manager.TickAsync(default);
        Check(f.Port.Requests.Count == 1 && f.Captures.Sum(c => c.Count) == 1, "global pause stops all captures");
        await f.Worker.SetAiPausedAsync(false, default); await f.Manager.TickAsync(default); Check(f.Port.Maximum == 1, "rapid resume still waits actual prior inference");
        f.Port.Release.SetResult(); f.Port.IgnoreCancellation = false; await Until(() => !f.Manager.List().Monitors.Any(m => m.Processing));
        await f.RunOne(); Check(f.Manager.List().Monitors.All(m => m.Active), "pause cancellation is not misclassified as timeout after resume");
        f.Port.IgnoreCancellation = true; f.Port.Release = new(TaskCreationOptions.RunContinuationsAsynchronously); f.Clock.Advance(31); await f.Manager.TickAsync(default); await Until(() => f.Manager.List().Monitors.Any(m => m.Processing));
        var timed = f.Manager.List().Monitors.Single(m => m.Processing).Definition.MonitorId;
        f.Clock.Advance(601); await f.Manager.TickAsync(default); Check(f.Manager.List().Monitors.Single(m => m.Definition.MonitorId == timed).Processing, "deadline cancellation also retains occupied slot");
        f.Port.Release.SetResult(); f.Port.IgnoreCancellation = false; await Until(() => !f.Manager.List().Monitors.Any(m => m.Processing));
        Check(!f.Manager.List().Monitors.Single(m => m.Definition.MonitorId == timed).Active, "ten minute deadline stops only its monitor");
        await f.RunOne(); Check(f.Manager.List().Monitors.Any(m => m.Active), "other monitor survives timeout");
        await using var small = await Fixture.Create(1200); var x = await small.Add("quota A"); var y = await small.Add("quota B");
        var session = Guid.NewGuid(); var target = Guid.NewGuid(); var external = small.Images.Register(session, target, small.Clock.GetUtcNow(), new(new byte[200], 10, 10));
        using (var protectedFrame = small.Images.Acquire(session, target, [external.Id]))
        { await small.Manager.StartAsync(x, default); await small.RunOne(); Check(small.Images.List(session, target).Count == 1 && !small.Manager.List().Monitors.Single(m => m.Definition.MonitorId == x).Active, "quota failure never deletes another session's leased image"); }
        small.Images.Remove(session, target, external.Id); await small.Manager.StartAsync(y, default); await small.RunOne(); Check(small.Manager.Detail(y).Comments.Count == 1, "another monitor proceeds when capacity becomes available");
    }
    public static async Task MigrationAndPersistenceFailure()
    {
        await using var f = await Fixture.Create(); var path = Path.Combine(f.Paths.SettingsDirectory, "worker-settings.json");
        var original = f.Worker.Configuration with { ScreenMonitor = new("vision", 120, "移行する指示"), ScreenMonitors = null };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(original, WorkerJsonContext.Default.WorkerConfiguration));
        var bytes = await File.ReadAllBytesAsync(path);
        using (var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        { await Expect<UnauthorizedAccessException>(() => f.Settings.LoadConfigurationAsync(default)); Check(bytes.SequenceEqual(await File.ReadAllBytesAsync(path)), "failed migration preserves original file bytes"); }
        var migrated = await f.Settings.LoadConfigurationAsync(default); var id = migrated.ScreenMonitors!.Single().MonitorId;
        Check(migrated.ScreenMonitors!.Single().Settings == original.ScreenMonitor && (await f.Settings.LoadConfigurationAsync(default)).ScreenMonitors!.Single().MonitorId == id, "migration retains settings and stable ID on repeated startup");
        await f.Settings.SaveConfigurationAsync(migrated with { ScreenMonitors = [] }, default);
        Check((await f.Settings.LoadConfigurationAsync(default)).ScreenMonitors!.Count == 0, "empty collection never resurrects legacy single settings");
        var release = new AppDataPaths(Path.Combine(f.Root, "r"), Path.Combine(f.Root, "l"), AppDataEnvironment.Release);
        var debug = new AppDataPaths(Path.Combine(f.Root, "r"), Path.Combine(f.Root, "l"), AppDataEnvironment.Debug);
        Check(release.SettingsDirectory != debug.SettingsDirectory, "environment-specific migration paths");
    }
    public static async Task CommentQuotaAndReload()
    {
        await using var f = await Fixture.Create(); f.Port.Body = new string('あ', 60000);
        var a = await f.Add("A"); var b = await f.Add("B"); await f.Manager.StartAsync(a, default); await f.Manager.StartAsync(b, default);
        for (var i = 0; i < 14; i++) { f.Clock.Advance(31); await f.RunOne(); }
        var all = f.Manager.Detail(a).Comments.Concat(f.Manager.Detail(b).Comments).ToArray();
        Check(all.Sum(c => (long)c.Content.Length * 2) <= ScreenMonitorLimits.MaximumCommentBytes && all.Length < 14 && all.Select(c => c.Id).Distinct().Count() == all.Length, "global text quota evicts old IDs across all histories");
        f.Port.Body = "short";
        for (var i = 0; i < 405; i++) { f.Clock.Advance(31); await f.RunOne(); }
        Check(f.Manager.Detail(a).Comments.Count <= 200 && f.Manager.Detail(b).Comments.Count <= 200, "per-monitor count applies before shared bytes");
        var persisted = await f.Settings.LoadConfigurationAsync(default);
        Check(persisted.ScreenMonitors!.Count == 2 && !JsonSerializer.Serialize(persisted, WorkerJsonContext.Default.WorkerConfiguration).Contains("short"), "only definitions persist, never comments");
        var stale = f.Manager.List().Monitors[0].Definition;
        f.Manager.Stop(stale.MonitorId); await f.Manager.TickAsync(default);
        await f.Manager.UpdateAsync(stale.MonitorId, new("rename", stale.Settings, stale.Revision), default);
        await Expect<WorkerException>(() => f.Manager.UpdateAsync(stale.MonitorId, new("stale", stale.Settings, stale.Revision), default));
    }
    public static async Task ViewSwitchAndHistoryEviction()
    {
        await using var f = await Fixture.Create(); var a = await f.Add("A"); var b = await f.Add("B");
        await f.Manager.StartAsync(a, default); await f.Manager.StartAsync(b, default);
        for (var i = 0; i < 4; i++) { f.Clock.Advance(31); await f.RunOne(); }
        var client = new UiClient(f.Manager); using var vm = new ScreenMonitorViewModel(client, f.Port);
        await vm.RefreshCommand.ExecuteAsync(); var first = vm.Monitors.Single(m => m.Id == a); var second = vm.Monitors.Single(m => m.Id == b);
        vm.Selected = first; await vm.PollAsync(); vm.Prompt = "A の下書き";
        vm.Selected = second; await vm.PollAsync(); vm.Prompt = "B の下書き";
        vm.Selected = first; await vm.PollAsync(); Check(vm.Prompt == "A の下書き", "independent drafts survive switching and polling");
        var oldId = vm.Comments[0].Id; client.HideComment = oldId; await vm.PollAsync();
        Check(vm.Comments.All(m => m.Id != oldId) && vm.Comments.Count == f.Manager.Detail(a).Comments.Count - 1, "server retention deletion removes the same UI ID without rebuilding other histories");
        var pending = new TaskCompletionSource<ScreenMonitorDetail>(TaskCreationOptions.RunContinuationsAsynchronously); client.NextDetail = pending.Task;
        vm.Selected = second; vm.Selected = first; await vm.PollAsync(); pending.SetResult(f.Manager.Detail(b)); await Task.Delay(10);
        Check(ReferenceEquals(vm.Selected, first) && vm.Prompt == "A の下書き" && vm.Comments.All(m => f.Manager.Detail(a).Comments.Any(c => c.Id == m.Id)), "late detail for B never overwrites A");
        var delayedList = new TaskCompletionSource<ScreenMonitorList>(TaskCreationOptions.RunContinuationsAsynchronously); client.NextList = delayedList.Task;
        var oldPoll = vm.PollAsync(); await vm.RefreshCommand.ExecuteAsync(); delayedList.SetException(new WorkerException("old connection failure")); await oldPoll;
        Check(!vm.State.Contains("未接続"), "old failed poll cannot overwrite newer successful refresh");
        await f.Manager.DeleteAsync(b, default); await vm.PollAsync(); Check(ReferenceEquals(vm.Selected, first) && vm.Monitors.Count == 1, "deleting B preserves selection, draft and history of A");
    }
    private sealed class UiClient(MultiScreenMonitorService service) : IMultiScreenMonitorClient
    {
        public Guid? HideComment; public Task<ScreenMonitorDetail>? NextDetail; public Task<ScreenMonitorList>? NextList;
        public Task<ScreenMonitorList> ListMonitorsAsync(CancellationToken token = default) { var pending = NextList; NextList = null; return pending ?? Task.FromResult(service.List()); }
        public Task<ScreenMonitorDetail> GetMonitorAsync(Guid id, CancellationToken token = default) { var pending = NextDetail; NextDetail = null; var value = service.Detail(id); return pending ?? Task.FromResult(value with { Comments = value.Comments.Where(c => c.Id != HideComment).ToArray() }); }
        public Task<ScreenMonitorOverview> CreateMonitorAsync(string name, CancellationToken token = default) => service.CreateAsync(name, token);
        public Task<ScreenMonitorOverview> UpdateMonitorAsync(Guid id, UpdateScreenMonitorRequest request, CancellationToken token = default) => service.UpdateAsync(id, request, token);
        public Task<ScreenMonitorOverview> PickMonitorAsync(Guid id, CancellationToken token = default) => service.PickAsync(id, token);
        public Task<ScreenMonitorOverview> StartMonitorAsync(Guid id, CancellationToken token = default) => service.StartAsync(id, token);
        public Task<ScreenMonitorOverview> StopMonitorAsync(Guid id, CancellationToken token = default) => Task.FromResult(service.Stop(id));
        public Task<ScreenMonitorList> DeleteMonitorAsync(Guid id, CancellationToken token = default) => service.DeleteAsync(id, token);
        public Task<ScreenMonitorList> StopAllMonitorsAsync(CancellationToken token = default) => Task.FromResult(service.StopAll());
    }
    private static void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); }
    private static async Task Until(Func<bool> condition) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(1, timeout.Token); }
    private static async Task Expect<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private sealed class Clock : TimeProvider
    { private long _ticks = DateTimeOffset.UtcNow.UtcTicks; public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero); public void Advance(int seconds) => Interlocked.Add(ref _ticks, TimeSpan.FromSeconds(seconds).Ticks); }
    private sealed class Capture : IScreenCaptureService
    {
        public int Count; public string? TargetName { get; private set; }
        public event EventHandler? TargetClosed;
        public void Close() { TargetName = null; TargetClosed?.Invoke(this, EventArgs.Empty); }
        public TaskCompletionSource? Selection;
        public async Task<bool> PickAsync(CancellationToken token) { if (Selection is not null) await Selection.Task; token.ThrowIfCancellationRequested(); TargetName = "同一の検証対象"; return true; }
        public Task<ChatImage> CaptureAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Count++; return Task.FromResult(new ChatImage([1, 2], 2, 1)); }
        public void Dispose() { TargetName = null; TargetClosed = null; }
    }
    private sealed class Port : IChatService, IModelControlService, IModelUseGate, IContentSource
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly HashSet<string> _loaded = [];
        public readonly List<(string Model, string Prompt)> Requests = [];
        public readonly List<string> Unloads = [];
        public string Body = "# コメント"; public int Concurrent, Maximum; public bool IgnoreCancellation; public string? FailModel;
        public TaskCompletionSource? Release;
        public Task<IReadOnlyList<FeedArticle>> FetchAsync(SourceConfiguration source, CancellationToken token) => Task.FromResult<IReadOnlyList<FeedArticle>>([]);
        public Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(["vision", "other"]);
        public async IAsyncEnumerable<string> StreamReplyAsync(string model, IReadOnlyList<ChatMessage> messages, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            lock (Requests) Requests.Add((model, messages.Single().Content)); var current = Interlocked.Increment(ref Concurrent); Maximum = Math.Max(Maximum, current);
            try { yield return "#\n"; if (Release is not null) { if (IgnoreCancellation) await Release.Task; else await Release.Task.WaitAsync(cancellationToken); } if (model == FailModel) throw new ChatServiceException("画像非対応"); yield return Body; }
            finally { Interlocked.Decrement(ref Concurrent); }
        }
        public Task<IReadOnlyList<LoadedModel>> GetLoadedModelsAsync(CancellationToken cancellationToken = default) { lock (_loaded) return Task.FromResult<IReadOnlyList<LoadedModel>>(_loaded.Select(m => new LoadedModel(m, "hash", 1, 1, "Q4", null)).ToArray()); }
        public Task LoadModelAsync(string model, CancellationToken cancellationToken = default) { lock (_loaded) _loaded.Add(model); return Task.CompletedTask; }
        public Task UnloadModelAsync(string model, CancellationToken cancellationToken = default) { lock (_loaded) _loaded.Remove(model); Unloads.Add(model); return Task.CompletedTask; }
        public async Task<IDisposable> EnterAsync(CancellationToken token) { await _gate.WaitAsync(token); return new Lease(_gate); }
        private sealed class Lease(SemaphoreSlim gate) : IDisposable { public void Dispose() => gate.Release(); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public string Root = Path.Combine(Path.GetTempPath(), "ateliers-multi-tests-" + Guid.NewGuid().ToString("N"));
        public AppDataPaths Paths = null!; public WorkerStateStore Settings = null!; public WorkerCoordinator Worker = null!; public MultiScreenMonitorService Manager = null!;
        public Clock Clock = new(); public Port Port = new(); public List<Capture> Captures = []; public CapturedImageStore Images = null!;
        public static async Task<Fixture> Create(int bytes = 128 * 1024 * 1024)
        {
            var f = new Fixture(); f.Paths = new(Path.Combine(f.Root, "r"), Path.Combine(f.Root, "l")); var log = new FileDiagnosticLog(f.Paths, "Worker");
            var db = new SqliteWebMonitoringStore(f.Paths, log); f.Settings = new(f.Paths, db, new JsonUserSettingsStore(f.Paths, log));
            f.Worker = new(f.Settings, db, f.Port, new ArticleMarkdownWriter(f.Paths), f.Port, f.Port, f.Port, log, log, f.Clock);
            await f.Worker.InitializeAsync(default); await f.Worker.ConfigureAsync(f.Worker.Configuration with { AllowLoad = true, AllowUnload = true }, default);
            f.Images = new(new(MaximumBytes: bytes), f.Clock);
            f.Manager = new(() => { var c = new Capture(); f.Captures.Add(c); return c; }, f.Port, f.Port, f.Port, f.Images, f.Worker, log, f.Clock);
            return f;
        }
        public async Task<Guid> Add(string name, string model = "vision", int period = 10, string prompt = "説明")
        {
            var item = await Manager.CreateAsync(name, default); var id = item.Definition.MonitorId;
            await Manager.UpdateAsync(id, new(name, new(model, period, prompt), item.Definition.Revision), default);
            await Manager.PickAsync(id, default); await Until(() => !Manager.List().Monitors.Single(m => m.Definition.MonitorId == id).Picking); return id;
        }
        public async Task RunOne() { await Manager.WaitForIdleAsync(); await Manager.TickAsync(default); await Manager.WaitForIdleAsync(); }
        public async ValueTask DisposeAsync()
        {
            Port.Release?.TrySetResult(); await Manager.DisposeAsync();
            var full = Path.GetFullPath(Root); if (Path.GetDirectoryName(full) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(full).StartsWith("ateliers-multi-tests-", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe test root");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
}
