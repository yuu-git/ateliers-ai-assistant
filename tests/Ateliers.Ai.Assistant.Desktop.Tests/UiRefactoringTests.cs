using Ateliers.Ai.Assistant.Application.Notifications;
using Ateliers.Ai.Assistant.Application.WebMonitoring;
using Ateliers.Ai.Assistant.Application.Worker;
using Ateliers.Ai.Assistant.Desktop.ViewModels;
using Ateliers.Ai.Assistant.Infrastructure.Storage;

namespace Ateliers.Ai.Assistant.Desktop.Tests;

/// <summary>取得・永続化を変更せず、絞込と編集中の状態を維持する表示契約を検証する。</summary>
internal static class UiRefactoringTests
{
    public static async Task FiltersAndSelection()
    {
        using var f = new Fixture();
        var api = new Client();
        using var session = new SessionOwner();
        using var web = new WebMonitoringViewModel(api, api, session.Session, session.Session, f.Settings, f.Log, action => action(), (_, _) => Task.CompletedTask);
        var a = api.Configuration.Sources[0]; var b = api.Configuration.Sources[1];
        api.Notifications = Enumerable.Range(0, 100).Select(index => new ArticleNotification(new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-index),
            NotificationKind.Information, "記事 " + index, "# 要約\n\n本文"), "https://example.com/shared-article", "article.md", SummaryModel: "model",
            FeedSourceUrl: index == 99 ? "https://example.com/deleted" : index % 2 == 0 ? a.Url : b.Url)).ToArray();
        web.UpdateSources(api.Configuration.Sources.Select(source => new WorkerSourceViewModel(source, null)).ToArray());
        await web.RefreshFeedAsync();
        Check(web.Notifications.Count == 100 && web.VisibleNotifications.Count == 100 && web.RangeText.Contains("全情報源の最新100件"), "global latest 100 scope");
        var source = web.SourceChoices[1]; web.SelectedFeedSource = source;
        Check(web.VisibleNotifications.Count == 50 && web.VisibleNotifications.All(row => row.Item.FeedSourceUrl == a.Url), "filter uses feed URL, not identical article URL");
        var selected = web.VisibleNotifications[0]; web.SelectedNotification = selected;
        var changes = 0; web.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(web.SelectedDocument)) changes++; };
        await web.RefreshFeedAsync();
        web.UpdateSources(api.Configuration.Sources.Select(item => new WorkerSourceViewModel(item with { Name = item.Name + " 更新" }, new(item.Id, NextDueAt: DateTimeOffset.UtcNow.AddHours(1)))).ToArray());
        Check(ReferenceEquals(web.SelectedFeedSource, source) && ReferenceEquals(web.SelectedNotification, selected) && changes == 0, "poll and source rename preserve identity and Markdown scroll contract");
        var deleting = 0;
        web.RequestArticleDeletionAsync = visible => { deleting = visible.Count; return Task.FromResult<IReadOnlyList<Guid>?>(visible.Select(row => row.Item.Notification.Id).ToArray()); };
        await web.DeleteArticlesCommand.ExecuteAsync();
        Check(deleting == 50 && api.Notifications.Count == 50 && web.SelectedNotification is null, "bulk delete only visible source entries");
        web.SelectedFeedSource = web.SourceChoices[2];
        web.UpdateSources([new(a, null)]);
        Check(web.SelectedFeedSource!.Id == Guid.Empty && web.VisibleNotifications.Count == 50 && web.VisibleNotifications.Any(row => row.Item.FeedSourceUrl!.EndsWith("deleted")), "removed sources remain in all notifications");
    }
    public static async Task EditingAndRefresh()
    {
        using var f = new Fixture(); var api = new Client();
        using var vm = new WorkerViewModel(api, api, new TestChatService(), f.Log);
        var initialPrompts = 0;
        vm.ConfirmDiscardEditsAsync = () => { initialPrompts++; return Task.FromResult(false); };
        vm.Model = "初回接続前の入力";
        await vm.InitializeAsync();
        Check(vm.Model == "初回接続前の入力" && vm.HasAiEdits && initialPrompts == 0, "automatic first connection preserves drafts without prompting for reload");
        vm.ConfirmDiscardEditsAsync = () => Task.FromResult(true); await vm.RefreshAsync();
        vm.SelectedSource = vm.Sources[0];
        var selected = vm.SelectedSource;
        vm.Name = "情報源の未保存入力"; vm.Model = "AI の未保存入力"; vm.AllowLoad = true;
        await vm.PollAsync();
        Check(ReferenceEquals(vm.SelectedSource, selected) && vm.Name == "情報源の未保存入力" && vm.Model == "AI の未保存入力" && vm.HasAiEdits && vm.HasSourceEdits, "poll preserves both drafts and source identity");
        api.Fetching = true; await vm.PollAsync();
        Check(vm.StatusSummary.Contains("取得: 巡回中") && vm.StatusSummary.Contains("AI:"), "source acquisition progress and AI state are separate");
        api.Configuration = api.Configuration with { Version = api.Configuration.Version + 1, ScreenMonitor = new("vision", 30) };
        await vm.PollAsync();
        await vm.SaveOptionsCommand.ExecuteAsync();
        Check(!vm.HasError && api.Configuration.ScreenMonitor?.IntervalSeconds == 30, "screen-only save rebases version without losing other drafts or screen settings");
        Check(api.Configuration.Sources[0].Name == "Source A" && vm.Name == "情報源の未保存入力" && vm.HasSourceEdits && !vm.HasAiEdits, "AI save does not save or discard source draft");
        vm.Model = "次の AI 入力";
        await vm.SaveSourceCommand.ExecuteAsync();
        Check(api.Configuration.SummaryModel == "AI の未保存入力" && vm.Model == "次の AI 入力" && vm.HasAiEdits && !vm.HasSourceEdits, "source save preserves AI draft");
        var sourceId = vm.SelectedSource!.Id; var name = vm.Name;
        await vm.FetchSourceAsync(api.Configuration.Sources.First(source => source.Id != sourceId).Id);
        Check(vm.SelectedSource.Id == sourceId && vm.Name == name, "manual browse-source fetch does not change editor selection");
        vm.ConfirmDiscardEditsAsync = () => Task.FromResult(false); await vm.RefreshAsync();
        Check(vm.Model == "次の AI 入力", "reload cancellation preserves draft");
        vm.ConfirmDiscardEditsAsync = () => Task.FromResult(true); await vm.RefreshAsync();
        Check(!vm.HasAiEdits && vm.Model == "AI の未保存入力", "explicit confirmed reload restores saved values");
        vm.Model = "遅い保存"; api.Saving = new(TaskCreationOptions.RunContinuationsAsynchronously); api.ReleaseSave = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowSave = vm.SaveOptionsCommand.ExecuteAsync(); await api.Saving.Task;
        var fetchCount = api.Fetches; await vm.FetchSourceAsync(api.Configuration.Sources[0].Id);
        Check(api.Fetches == fetchCount, "manual fetch cannot overlap a settings operation");
        vm.Model = "保存中の新しい編集";
        api.ReleaseSave.SetResult(); await slowSave; api.Saving = null;
        Check(vm.Model == "保存中の新しい編集" && vm.HasAiEdits && api.Configuration.SummaryModel == "遅い保存", "late save completion preserves newer editing");
        vm.Model = "失敗しても残す入力"; api.FailSave = true; await vm.SaveOptionsCommand.ExecuteAsync();
        Check(vm.HasError && vm.Connected && vm.Model == "失敗しても残す入力" && vm.HasAiEdits, "save failure preserves editable draft and connection");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class SessionOwner : IDisposable
    {
        public Ateliers.Ai.Assistant.Application.Models.ChatModelSession Session { get; } = new(new TestChatService(), new TestModelControl());
        public void Dispose() { }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ateliers-ui-tests-" + Guid.NewGuid().ToString("N"));
        public FileDiagnosticLog Log { get; }
        public SettingsViewModel Settings { get; }
        public Fixture()
        {
            var paths = new AppDataPaths(Path.Combine(_root, "r"), Path.Combine(_root, "l")); Log = new(paths);
            Settings = new(paths, new JsonUserSettingsStore(paths, Log), Log, _ => Task.CompletedTask);
        }
        public void Dispose()
        {
            var path = Path.GetFullPath(_root);
            if (Path.GetDirectoryName(path) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(path).StartsWith("ateliers-ui-tests-")) throw new InvalidOperationException("Unexpected temporary directory");
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
    private sealed class Client : IWorkerClient, IWorkerLauncher, IWebMonitoringReader, IWebCollectionOperations
    {
        public WorkerConfiguration Configuration = WorkerConfiguration.Default with { SummaryModel = "model", Sources = [new(Guid.NewGuid(), "Source A", "https://example.com/a"), new(Guid.NewGuid(), "Source B", "https://example.com/b")] };
        public IReadOnlyList<ArticleNotification> Notifications = [];
        public bool FailSave, Fetching;
        public TaskCompletionSource? Saving, ReleaseSave;
        public int Fetches;
        public Task<WorkerStatus> GetStatusAsync(CancellationToken token = default) => Task.FromResult(new WorkerStatus(1, "4.0.1", Guid.Empty, DateTimeOffset.UtcNow, Configuration, [], 0, "待機中", "", Fetching, false));
        public async Task<WorkerConfiguration> SaveConfigurationAsync(WorkerConfiguration configuration, CancellationToken token = default)
        {
            if (Saving is not null) { Saving.SetResult(); await ReleaseSave!.Task; }
            if (FailSave) throw new WorkerException("検証用の保存失敗");
            if (configuration.Version != Configuration.Version) throw new WorkerException("設定競合");
            Configuration = configuration with { Version = configuration.Version + 1, Sources = configuration.Sources.Where(source => !source.Deleted).ToArray() }; return Configuration;
        }
        public Task<FetchAccepted> FetchAsync(FetchRequest request, CancellationToken token = default) { Fetches++; return Task.FromResult(new FetchAccepted(request.RequestId, false)); }
        public Task<FetchAccepted> StopAsync(CancellationToken token = default) => Task.FromResult(new FetchAccepted(Guid.Empty, false));
        public Task<IReadOnlyList<CollectionRun>> GetRunsAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<CollectionRun>>([]);
        public Task<IReadOnlyList<ArticleNotification>> GetNotificationsAsync(CancellationToken token = default) => Task.FromResult(Notifications);
        public Task<ArticleDeletionResult> DeleteArticlesAsync(IReadOnlyList<Guid> ids, CancellationToken token = default)
        { Notifications = Notifications.Where(item => !ids.Contains(item.Notification.Id)).ToArray(); return Task.FromResult(new ArticleDeletionResult(ids, [], false, "")); }
        public Task<IReadOnlyList<CollectionRun>> RunsAsync(CancellationToken token) => GetRunsAsync(token);
        public Task<IReadOnlyList<ArticleNotification>> NotificationsAsync(CancellationToken token) => GetNotificationsAsync(token);
        public Task<ArticleDeletionResult> DeleteAsync(IReadOnlyList<Guid> ids, CancellationToken token) => DeleteArticlesAsync(ids, token);
        public Task CollectAsync(string url, string? model, IProgress<string>? progress, CancellationToken token) => Task.CompletedTask;
        public Task StartAsync() => Task.CompletedTask;
        public Task<WorkerStartupState> GetStartupAsync() => Task.FromResult(new WorkerStartupState(false, "未登録"));
        public Task<WorkerStartupState> SetStartupAsync(bool enabled) => GetStartupAsync();
    }
}
