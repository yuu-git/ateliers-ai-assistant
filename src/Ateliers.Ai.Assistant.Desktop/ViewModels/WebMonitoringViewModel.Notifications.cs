using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Ateliers.Ai.Assistant.Application.WebMonitoring;

namespace Ateliers.Ai.Assistant.Desktop.ViewModels;

/// <summary>通知管理の表示状態。DB 条件・永続化は Worker のポートへ委譲する。</summary>
public sealed partial class WebMonitoringViewModel
{
    private readonly INotificationManagement? _notificationManagement;
    private readonly TimeProvider _clock;
    private int _filterIndex, _matchingCount, _totalUnread;
    private string _tagFilterInput = "", _tagInput = "";
    private string? _activeTagName;
    private ArticleTag? _selectedTagToRemove;
    private IReadOnlyList<SourceUnreadCount> _unreadBySource = [];
    private Guid? _openedArticleId;
    private WebNotificationViewModel? _autoReadArticle;
    private readonly Queue<WebNotificationViewModel> _pendingReadArticles = new();
    private readonly HashSet<Guid> _pendingReadIds = new();
    private Task _readDrain = Task.CompletedTask;
    private bool _drainingReads;
    private TaskCompletionSource? _metadataIdle;
    private TaskCompletionSource? _refreshIdle;
    private long _queryRevision;
    private bool _queryPending, _metadataBusy;
    private bool _hasUnreadCounts;
    /// <summary>未取得の0件を実件数と誤認させないための集計状態。</summary>
    public bool HasUnreadCounts { get => _hasUnreadCounts; private set => SetProperty(ref _hasUnreadCounts, value); }
    /// <summary>通常 Worker の通知管理 API が接続されているか。</summary>
    public bool HasNotificationManagement => _notificationManagement is not null;
    /// <summary>別のメタデータ変更中は二重実行しない。</summary>
    public bool CanManageNotification => HasNotificationManagement && !_disposed && !_metadataBusy && !IsRunning;
    /// <summary>DB 全表示対象通知から集計した未読総数。削除済み情報源も含む。</summary>
    public int TotalUnread { get => _totalUnread; private set => SetProperty(ref _totalUnread, value); }
    /// <summary>折りたたみ中も現在の検索条件を明示する。</summary>
    public string FilterSummary => "表示条件: " + (FilterIndex == 1 ? "未読のみ" : FilterIndex == 2 ? "ブックマークのみ" : "すべて") + (_activeTagName is null ? " / タグ指定なし" : " / タグ: " + _activeTagName);
    /// <summary>0:すべて、1:未読、2:ブックマーク。</summary>
    public int FilterIndex
    {
        get => _filterIndex;
        set { if (value is >= 0 and <= 2 && value != _filterIndex) { EndReadingSelection(); SetProperty(ref _filterIndex, value); ApplyFilter(); QueryChanged(); } }
    }
    /// <summary>タグ検索の入力。適用するまで検索条件を変更しない。</summary>
    public string TagFilterInput { get => _tagFilterInput; set => SetProperty(ref _tagFilterInput, value); }
    /// <summary>詳細から付与するタグ名。候補にない既存名も手入力で再利用できる。</summary>
    public string TagInput { get => _tagInput; set => SetProperty(ref _tagInput, value); }
    /// <summary>タグ解除の明示対象。</summary>
    public ArticleTag? SelectedTagToRemove { get => _selectedTagToRemove; set { if (SetProperty(ref _selectedTagToRemove, value)) NotifyMetadataCommands(); } }
    /// <summary>最大500件のタグ名候補。手入力はこの上限に依存しない。</summary>
    public ObservableCollection<string> TagNames { get; } = [];
    /// <summary>ローカル日付で区切った一覧。同じグループ・行を更新でも再利用する。</summary>
    public ObservableCollection<NotificationDateGroup> NotificationGroups { get; } = [];
    /// <summary>タグ条件を DB 検索へ適用する。</summary>
    public AsyncCommand ApplyTagFilterCommand { get; private set; } = null!;
    /// <summary>閲覧する情報源を維持して、未読・タグ等の条件を解除する。</summary>
    public AsyncCommand ClearNotificationFiltersCommand { get; private set; } = null!;
    /// <summary>選択記事を未読／既読へ戻す明示操作。</summary>
    public AsyncCommand ToggleReadCommand { get; private set; } = null!;
    /// <summary>詳細のブックマーク切替。</summary>
    public AsyncCommand ToggleSelectedBookmarkCommand { get; private set; } = null!;
    /// <summary>選択記事へ手動タグを追加・再利用する。</summary>
    public AsyncCommand AddTagCommand { get; private set; } = null!;
    /// <summary>選択記事とのタグ関連だけを解除する。</summary>
    public AsyncCommand RemoveTagCommand { get; private set; } = null!;
    private void InitializeNotificationCommands()
    {
        ApplyTagFilterCommand = new(async () =>
        {
            try { var tag = string.IsNullOrWhiteSpace(TagFilterInput) ? null : TagNameRules.Normalize(TagFilterInput).Name; EndReadingSelection(); _activeTagName = tag; ApplyFilter(); QueryChanged(false); await RefreshFeedAsync(); }
            catch (Exception exception) { Failure(exception); }
        }, () => CanManageNotification);
        ClearNotificationFiltersCommand = new(async () =>
        {
            EndReadingSelection(); _filterIndex = 0; OnPropertyChanged(nameof(FilterIndex)); _activeTagName = null; TagFilterInput = "";
            ApplyFilter(); QueryChanged(false); await RefreshFeedAsync();
        }, () => !_disposed);
        ToggleReadCommand = new(ToggleExplicitReadAsync,
            () => CanManageNotification && SelectedNotification is not null);
        ToggleSelectedBookmarkCommand = new(() => SelectedNotification is { } row ? ToggleBookmarkAsync(row) : Task.CompletedTask,
            () => CanManageNotification && SelectedNotification is not null);
        AddTagCommand = new(async () =>
        {
            if (SelectedNotification is not { } row) return;
            var input = TagInput;
            var succeeded = await MutateNotificationAsync(() => _notificationManagement!.AddTagAsync(new(row.Item.Notification.Id, TagNameRules.Normalize(input).Name)));
            if (succeeded && TagInput == input) TagInput = "";
        }, () => CanManageNotification && SelectedNotification is not null);
        RemoveTagCommand = new(() => SelectedNotification is { } row && SelectedTagToRemove is { } tag ?
            MutateNotificationAsync(() => _notificationManagement!.RemoveTagAsync(new(row.Item.Notification.Id, tag.Id))) : Task.CompletedTask,
            () => CanManageNotification && SelectedNotification is not null && SelectedTagToRemove is not null);
    }
    /// <summary>開いている間は未読を維持し、明示的に開いた記事だけ閲覧終了時の既読化を予約する。</summary>
    public async Task OpenArticleAsync(WebNotificationViewModel row)
    {
        if (_disposed || _openedArticleId == row.Item.Notification.Id) return;
        SelectedNotification = row;
        _openedArticleId = row.Item.Notification.Id;
        _autoReadArticle = HasNotificationManagement && row.IsUnread ? row : null;
        await DrainPendingReadsAsync();
    }
    /// <summary>詳細を閉じる・画面を離れる操作。処理中のメタデータ更新と順序を合わせる。</summary>
    public async Task EndReadingAsync()
    {
        EndReadingSelection();
        if (_metadataIdle is { } idle) await idle.Task;
        await _readDrain;
        await DrainPendingReadsAsync();
    }
    /// <summary>明示的な既読／未読指定を、その閲覧セッションの自動既読化より優先する。</summary>
    private async Task ToggleExplicitReadAsync()
    {
        if (SelectedNotification is not { } row) return;
        var automatic = _autoReadArticle;
        _autoReadArticle = null;
        if (!await MutateNotificationAsync(() => _notificationManagement!.SetReadAsync(new(row.Item.Notification.Id, row.IsUnread)), row) &&
            _openedArticleId == row.Item.Notification.Id) _autoReadArticle = automatic;
    }
    /// <summary>開く操作で予約した記事だけをキューへ移す。初期選択・自動選択には副作用を付けない。</summary>
    private void FinishReading()
    {
        var row = _autoReadArticle; _autoReadArticle = null; _openedArticleId = null;
        if (row is { IsUnread: true } && _pendingReadIds.Add(row.Item.Notification.Id)) _pendingReadArticles.Enqueue(row);
        ResumePendingRead();
    }
    /// <summary>表示条件や情報源の切替は、現在の記事の閲覧を終えて詳細を閉じる。</summary>
    private void EndReadingSelection() { FinishReading(); SelectedNotification = null; }
    /// <summary>一覧のボタンからの操作。記事選択・既読状態を変えない。</summary>
    public async Task ToggleBookmarkAsync(WebNotificationViewModel row)
    { if (CanManageNotification) await MutateNotificationAsync(() => _notificationManagement!.SetBookmarkAsync(new(row.Item.Notification.Id, !row.Item.IsBookmarked))); }
    /// <summary>付与済みタグを選ぶ明示操作。記事を既読にしない。</summary>
    public async Task FilterByTagAsync(ArticleTag tag) { TagFilterInput = tag.Name; await ApplyTagFilterCommand.ExecuteAsync(); }
    private async Task<bool> MutateNotificationAsync(Func<Task<ArticleMetadata>> operation, WebNotificationViewModel? target = null)
    {
        if (!CanManageNotification) return false;
        _metadataBusy = true; _metadataIdle = new(TaskCreationOptions.RunContinuationsAsynchronously); _queryRevision++; NotifyMetadataCommands();
        try
        {
            var metadata = await operation();
            if (_disposed) return false;
            var rows = Notifications.Concat(SelectedNotification is { } selected ? new[] { selected } : Array.Empty<WebNotificationViewModel>())
                .Concat(target is null ? Array.Empty<WebNotificationViewModel>() : new[] { target }).Distinct();
            foreach (var row in rows.Where(row => row.Item.Notification.Id == metadata.ArticleId))
                row.Update(row.Item with { ReadAt = metadata.ReadAt, IsBookmarked = metadata.IsBookmarked, Tags = metadata.Tags });
            QueryChanged(false); await RefreshFeedAsync(); return true;
        }
        catch (Exception exception) { Failure(exception); return false; }
        finally
        {
            _metadataBusy = false; var idle = _metadataIdle; _metadataIdle = null; idle?.TrySetResult(); NotifyMetadataCommands();
            ResumePendingRead();
        }
    }
    /// <summary>削除された記事を既読化せず、遅れて到着した操作から除外する。</summary>
    private void ForgetReading(IReadOnlyList<Guid> deleted)
    {
        if (_autoReadArticle is { } row && deleted.Contains(row.Item.Notification.Id)) _autoReadArticle = null;
        _pendingReadIds.ExceptWith(deleted);
    }
    /// <summary>他の更新・削除が終わってから、閲覧終了した記事の既読化を直列に再開する。</summary>
    private void ResumePendingRead() => _ = DrainPendingReadsAsync();
    /// <summary>進行中なら同じ完了を返し、別操作中ならその終了通知で再開する。</summary>
    private Task DrainPendingReadsAsync()
    {
        if (_drainingReads) return _readDrain;
        if (!CanManageNotification || _pendingReadArticles.Count == 0) return Task.CompletedTask;
        _drainingReads = true;
        return _readDrain = DrainReadsCoreAsync();
    }
    /// <summary>閲覧を終えた記事を順に確定する。失敗を成功扱いにせず未読のまま残す。</summary>
    private async Task DrainReadsCoreAsync()
    {
        // 選択 setter／表示条件の更新を完了してからAPI結果を反映する。
        await Task.Yield();
        try
        {
            while (CanManageNotification && _pendingReadArticles.TryDequeue(out var row))
            {
                if (!_pendingReadIds.Remove(row.Item.Notification.Id) || !row.IsUnread) continue;
                if (!await MutateNotificationAsync(() => _notificationManagement!.SetReadAsync(new(row.Item.Notification.Id, true)), row)) break;
            }
        }
        finally { _drainingReads = false; }
    }
    private void QueryChanged(bool refresh = true)
    {
        if (_notificationManagement is null || _disposed) return;
        OnPropertyChanged(nameof(FilterSummary));
        _queryRevision++; _queryPending = true;
        if (refresh) _ = RefreshFeedAsync();
    }
    private void AcceptPage(NotificationPage page)
    {
        HasUnreadCounts = true; TotalUnread = page.TotalUnread; _matchingCount = page.MatchingCount; _unreadBySource = page.UnreadBySource;
        Synchronize(TagNames, page.TagCatalog.Select(tag => tag.Name).ToArray());
        ApplyUnreadCounts();
    }
    private void ApplyUnreadCounts()
    {
        if (!HasUnreadCounts) return;
        foreach (var source in SourceChoices)
            source.SetUnreadCount(source.Url is null ? TotalUnread : _unreadBySource.FirstOrDefault(count => count.FeedSourceUrl == source.Url)?.Count ?? 0);
    }
    private void UpdateDateGroups()
    {
        var today = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
        var grouped = VisibleNotifications.GroupBy(row => row.Item.FetchedAt is { } at ? (DateOnly?)DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, _clock.LocalTimeZone).DateTime) : null)
            .OrderByDescending(group => group.Key).ToArray();
        var desired = grouped.Select(group =>
        {
            var existing = NotificationGroups.FirstOrDefault(item => item.Date == group.Key) ?? new NotificationDateGroup(group.Key);
            existing.SetToday(today);
            Synchronize(existing.Items, group.OrderByDescending(row => row.Item.FetchedAt).ToArray());
            return existing;
        }).ToArray();
        Synchronize(NotificationGroups, desired);
    }
    private void NotifyMetadataCommands()
    {
        OnPropertyChanged(nameof(CanDeleteArticle)); DeleteArticlesCommand?.NotifyCanExecuteChanged();
        if (_selectedTagToRemove is { } tag && (SelectedNotification is null || !SelectedNotification.TagItems.Any(item => item.Id == tag.Id))) { _selectedTagToRemove = null; OnPropertyChanged(nameof(SelectedTagToRemove)); }
        OnPropertyChanged(nameof(CanManageNotification));
        ToggleReadCommand?.NotifyCanExecuteChanged(); ToggleSelectedBookmarkCommand?.NotifyCanExecuteChanged();
        AddTagCommand?.NotifyCanExecuteChanged(); RemoveTagCommand?.NotifyCanExecuteChanged(); ApplyTagFilterCommand?.NotifyCanExecuteChanged();
    }
}

/// <summary>初回取得のローカル日付に対応する、更新可能な表示グループ。</summary>
#if WINDOWS
[WinRT.GeneratedBindableCustomProperty]
#endif
public sealed partial class NotificationDateGroup(DateOnly? date) : ObservableObject
{
    /// <summary>不明な日時を公開日時等から推測しない。</summary>
    public DateOnly? Date { get; } = date;
    /// <summary>グループ内の通知。</summary>
    public ObservableCollection<WebNotificationViewModel> Items { get; } = [];
    /// <summary>今日／昨日／日付の見出し。</summary>
    public string Label { get; private set; } = "";
    /// <summary>日付境界をまたいだポーリングでも見出しを正しく更新する。</summary>
    public void SetToday(DateOnly today)
    {
        var label = Date is null ? "初回取得日時不明" : Date == today ? "今日" : Date == today.AddDays(-1) ? "昨日" : Date.Value.ToString("yyyy/MM/dd");
        if (Label != label) { Label = label; OnPropertyChanged(nameof(Label)); }
    }
}
