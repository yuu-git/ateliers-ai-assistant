using Ateliers.Ai.Assistant.Application.Models;
using Ateliers.Ai.Assistant.Application.WebMonitoring;
using Ateliers.Ai.Assistant.Application.Worker;
using Ateliers.Ai.Assistant.Desktop.ViewModels;
using Ateliers.Ai.Assistant.Infrastructure.Storage;

namespace Ateliers.Ai.Assistant.Desktop.Tests;

/// <summary>ユーザー操作と自動更新を区別する表示契約を実ストアと偽 Worker で検証する。</summary>
internal static class NotificationViewModelTests
{
    public static async Task ExplicitReadAndLocalGroups()
    {
        using var f = new NotificationManagementTests.Fixture(); await f.SeedAsync(3, 2);
        var today = new DateTimeOffset(2026, 1, 1, 15, 30, 0, TimeSpan.Zero);
        await f.SqlAsync("UPDATE WebArticles SET FetchedTicks=$f WHERE Key='key-0000'", ("$f", today.UtcTicks));
        await f.SqlAsync("UPDATE WebArticles SET FetchedTicks=$f WHERE Key='key-0001'", ("$f", today.AddHours(-1).UtcTicks));
        var clock = new Clock(new(2026, 1, 1, 16, 0, 0, TimeSpan.Zero));
        var metadata = new MetadataProbe(f.Store); var session = new ChatModelSession(new TestChatService(), new TestModelControl());
        var settings = new SettingsViewModel(f.Paths, new JsonUserSettingsStore(f.Paths, f.Log), f.Log, _ => Task.CompletedTask);
        using var vm = new WebMonitoringViewModel(new Operations(), f.Store, session, session, settings, f.Log, action => action(), (_, _) => Task.CompletedTask, metadata, clock);
        vm.UpdateSources(f.Sources.Take(2).Select((url, index) => new WorkerSourceViewModel(new(Guid.NewGuid(), "Source " + index, url), null)).ToArray());
        await vm.RefreshFeedAsync();
        Check(vm.NotificationGroups.Select(group => group.Label).SequenceEqual(new[] { "今日", "昨日" }) && vm.TotalUnread == 2, "local JST date groups use acquisition time, not later summary/notification time");
        var first = vm.VisibleNotifications[0]; var second = vm.VisibleNotifications[1];
        vm.SelectedNotification = first; await vm.RefreshFeedAsync();
        Check(metadata.ReadRequests == 0 && first.IsUnread, "initial or programmatic selection and polling do not mark read");
        var documentChanges = 0; vm.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(vm.SelectedDocument)) documentChanges++; };
        await vm.OpenArticleAsync(first);
        Check(metadata.ReadRequests == 1 && !first.IsUnread && vm.TotalUnread == 1 && documentChanges == 0, "explicit open persists read without replacing Markdown");
        await vm.ToggleBookmarkAsync(second);
        Check(second.IsUnread && second.Item.IsBookmarked && ReferenceEquals(vm.SelectedNotification, first) && metadata.ReadRequests == 1, "list bookmark does not select or read another article");
        vm.TagInput = "調査"; await vm.AddTagCommand.ExecuteAsync();
        Check(first.TagItems.Count == 1 && !first.IsUnread && documentChanges == 0, "tag edit updates metadata without Markdown reset");
        await vm.ToggleReadCommand.ExecuteAsync(); Check(first.IsUnread && vm.TotalUnread == 2, "explicit return to unread");
        vm.FilterIndex = 1; await Task.Delay(100); await vm.RefreshFeedAsync();
        await vm.OpenArticleAsync(first);
        await WaitUntil(() => vm.TotalUnread == 1 && vm.VisibleNotifications.Count == 1);
        Check(ReferenceEquals(vm.SelectedNotification, first) && !vm.VisibleNotifications.Contains(first), "opening unread item keeps its detail after it leaves unread results");
        metadata.FailBookmark = true; await vm.ToggleBookmarkAsync(first);
        Check(!first.Item.IsBookmarked && vm.HasError && ReferenceEquals(vm.SelectedNotification, first), "failed mutation does not predict success or discard selected detail");
    }
    public static async Task GlobalWorkerControls()
    {
        using var f = new NotificationManagementTests.Fixture(); var api = new RuntimeClient(); var clock = new Clock(DateTimeOffset.UtcNow);
        using var vm = new WorkerViewModel(api, api, new TestChatService(), f.Log, clock);
        await vm.InitializeAsync(); Check(vm.GlobalWorkerState.Contains("停止中") && vm.StartCommand.CanExecute(null), "actual absent process starts stopped");
        await vm.StartCommand.ExecuteAsync(); Check(vm.Connected && vm.GlobalWorkerState.Contains("稼働中"), "running only after successful status response");
        vm.SelectedSource = vm.Sources[0]; vm.Name = "情報源の未保存入力"; vm.Model = "モデルの未保存入力"; vm.AllowUnload = true;
        await vm.ToggleAiPauseCommand.ExecuteAsync();
        Check(api.Configuration.AiPaused && vm.ActualAiPaused && vm.AiPaused && api.Configuration.SummaryModel == "saved-model" && !api.Configuration.AllowUnload && vm.Model == "モデルの未保存入力" && vm.HasSourceEdits,
            "global pause persists only pause and shares actual state without saving other drafts");
        await vm.ToggleAiPauseCommand.ExecuteAsync(); Check(!vm.ActualAiPaused && !api.Configuration.AiPaused, "global resume restores actual policy");
        api.FailPause = true; await vm.ToggleAiPauseCommand.ExecuteAsync(); Check(!vm.ActualAiPaused && vm.HasError, "pause failure does not fake paused state"); api.FailPause = false;
        await vm.StopCommand.ExecuteAsync();
        Check(vm.GlobalWorkerState.Contains("停止処理中") && vm.CompactAiState == "AI ?" && !vm.StartCommand.CanExecute(null), "stop acknowledgement is not process exit and blocks double operations, including compact unknown AI state");
        clock.Advance(TimeSpan.FromSeconds(30)); await vm.PollAsync();
        Check(vm.GlobalWorkerState.Contains("停止未完了") && vm.StopCommand.CanExecute(null), "still responding process after deadline is not called stopped");
        api.Running = false; await vm.PollAsync(); Check(!vm.Connected && vm.GlobalWorkerState.Contains("停止中"), "released process handle confirms stopped");
        api.Running = true; api.Unreachable = true; await vm.PollAsync();
        Check(vm.GlobalWorkerState.Contains("接続不能") && !vm.StartCommand.CanExecute(null), "unreachable but present process is not reported stopped or started twice");
    }
    private static void Check(bool value, string text) { if (!value) throw new InvalidOperationException(text); }
    private static async Task WaitUntil(Func<bool> predicate) { for (var i = 0; i < 200; i++) { if (predicate()) return; await Task.Delay(10); } throw new TimeoutException("Notification updates did not complete"); }
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("Test JST", TimeSpan.FromHours(9), "Test JST", "Test JST");
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
    private sealed class Operations : IWebCollectionOperations
    {
        public Task CollectAsync(string url, string? model, IProgress<string>? progress, CancellationToken token) => Task.CompletedTask;
        public Task<ArticleDeletionResult> DeleteAsync(IReadOnlyList<Guid> ids, CancellationToken token) => Task.FromResult(new ArticleDeletionResult([], [], true, ""));
    }
    private sealed class MetadataProbe(INotificationManagement inner) : INotificationManagement
    {
        public int ReadRequests;
        public bool FailBookmark;
        public Task<NotificationPage> QueryAsync(NotificationQuery query, CancellationToken token = default) => inner.QueryAsync(query, token);
        public Task<ArticleMetadata> SetReadAsync(SetArticleReadRequest request, CancellationToken token = default) { ReadRequests++; return inner.SetReadAsync(request, token); }
        public Task<ArticleMetadata> SetBookmarkAsync(SetArticleBookmarkRequest request, CancellationToken token = default) => FailBookmark ? throw new WebMonitoringException("検証用の通信失敗") : inner.SetBookmarkAsync(request, token);
        public Task<ArticleMetadata> AddTagAsync(AddArticleTagRequest request, CancellationToken token = default) => inner.AddTagAsync(request, token);
        public Task<ArticleMetadata> RemoveTagAsync(RemoveArticleTagRequest request, CancellationToken token = default) => inner.RemoveTagAsync(request, token);
    }
    private sealed class RuntimeClient : IWorkerClient, IWorkerLauncher, IWorkerAiControl, IWorkerPresenceProbe
    {
        public bool Running, Unreachable, FailPause;
        public WorkerConfiguration Configuration = WorkerConfiguration.Default with { SummaryModel = "saved-model", Sources = [new(Guid.NewGuid(), "Source", "https://example.com/feed")] };
        public Task<WorkerStatus> GetStatusAsync(CancellationToken token = default)
        {
            if (!Running || Unreachable) throw new WorkerException("接続できません。", connectionFailure: true);
            return Task.FromResult(new WorkerStatus(1, WorkerCoordinator.ImplementationVersion, Guid.Empty, DateTimeOffset.UtcNow, Configuration, [], 0, "待機中", "", false, false));
        }
        public Task<WorkerPresence> GetPresenceAsync(CancellationToken token = default) => Task.FromResult(Running ? WorkerPresence.Running : WorkerPresence.Stopped);
        public Task<WorkerConfiguration> SetAiPausedAsync(bool paused, CancellationToken token = default)
        { if (FailPause) throw new WorkerException("検証用の保存失敗"); Configuration = Configuration with { Version = Configuration.Version + 1, AiPaused = paused }; return Task.FromResult(Configuration); }
        public Task<WorkerConfiguration> SaveConfigurationAsync(WorkerConfiguration configuration, CancellationToken token = default) { Configuration = configuration with { Version = Configuration.Version + 1 }; return Task.FromResult(Configuration); }
        public Task<FetchAccepted> FetchAsync(FetchRequest request, CancellationToken token = default) => Task.FromResult(new FetchAccepted(request.RequestId, false));
        public Task<FetchAccepted> StopAsync(CancellationToken token = default) => Task.FromResult(new FetchAccepted(Guid.Empty, false));
        public Task<IReadOnlyList<CollectionRun>> GetRunsAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<CollectionRun>>([]);
        public Task<IReadOnlyList<ArticleNotification>> GetNotificationsAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<ArticleNotification>>([]);
        public Task<ArticleDeletionResult> DeleteArticlesAsync(IReadOnlyList<Guid> ids, CancellationToken token = default) => Task.FromResult(new ArticleDeletionResult([], [], true, ""));
        public Task StartAsync() { Running = true; return Task.CompletedTask; }
        public Task<WorkerStartupState> GetStartupAsync() => Task.FromResult(new WorkerStartupState(false, "未登録"));
        public Task<WorkerStartupState> SetStartupAsync(bool enabled) => GetStartupAsync();
    }
}
