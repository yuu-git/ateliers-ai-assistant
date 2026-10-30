using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ateliers.Ai.Assistant.Application.Chat;
using Ateliers.Ai.Assistant.Application.Models;
using Ateliers.Ai.Assistant.Application.Storage;
using Ateliers.Ai.Assistant.Application.WebMonitoring;
using Ateliers.Ai.Assistant.Desktop.Presentation;
using Ateliers.Ai.Assistant.Application.Worker;

namespace Ateliers.Ai.Assistant.Desktop.ViewModels;

/// <summary>単一フィードの手動実行と永続結果の表示。収集・DB・HTTP の詳細はサービスへ委譲する。</summary>
#if WINDOWS
[WinRT.GeneratedBindableCustomProperty]
#endif
public sealed partial class WebMonitoringViewModel : ObservableObject, IDisposable
{
    private readonly IWebCollectionOperations _service;
    private readonly IWebMonitoringReader _store;
    private readonly IChatService _chat;
    private readonly IModelLifecycleService _lifecycle;
    private readonly SettingsViewModel _settings;
    private readonly IAppDiagnostics _diagnostics;
    private readonly Action<Action> _dispatch;
    private CancellationTokenSource? _runCancellation;
    private Task _running = Task.CompletedTask;
    private long _generation;
    private bool _busy, _disposed, _refreshing;
    private string _url = "", _state = "フィード URL と要約モデルを設定してください。", _error = "";
    private string? _model;
    private WebNotificationViewModel? _selected;
    private FeedSourceChoice? _selectedSource;

    /// <summary>ユースケースと表示用のポートをホストから受け取る。</summary>
    public WebMonitoringViewModel(IWebCollectionOperations service, IWebMonitoringReader store, IChatService chat,
        IModelLifecycleService lifecycle, SettingsViewModel settings, IAppDiagnostics diagnostics, Action<Action> dispatch,
        Func<ArticleNotification, bool, Task> openLink, INotificationManagement? notifications = null, TimeProvider? clock = null)
    {
        _service = service; _store = store; _chat = chat; _lifecycle = lifecycle; _settings = settings; _diagnostics = diagnostics; _dispatch = dispatch;
        _notificationManagement = notifications ?? service as INotificationManagement; _clock = clock ?? TimeProvider.System;
        FetchCommand = new(() => _running = CollectAsync(), () => CanEdit && !string.IsNullOrWhiteSpace(FeedUrl));
        SaveSettingsCommand = new(SaveConfigurationAsync, () => CanEdit);
        RefreshCommand = new(RefreshAsync, () => !_disposed && !IsRunning && !_refreshing);
        CancelCommand = new(() => _runCancellation?.Cancel(), () => IsRunning);
        OpenSourceCommand = new(() => OpenAsync(false, openLink), () => SelectedNotification?.Item.SourceUrl is not null);
        OpenMarkdownCommand = new(() => OpenAsync(true, openLink), () => SelectedNotification is not null);
        DeleteArticlesCommand = new(() => _running = DeleteArticlesAsync(), () => CanDeleteArticle && VisibleNotifications.Count != 0);
        RefreshFeedCommand = new(RefreshFeedAsync, () => !_disposed && !IsRunning && !_refreshing);
        _lifecycle.Changed += ModelStateChanged;
        SourceChoices.Add(new(Guid.Empty, "すべての通知", null, "全情報源の最新100件"));
        _selectedSource = SourceChoices[0];
        InitializeNotificationCommands();
    }
    /// <summary>入力中の単一フィード URL。</summary>
    public string FeedUrl { get => _url; set { if (SetProperty(ref _url, value)) NotifyCommands(); } }
    /// <summary>チャットと独立して選択する要約モデル。選択だけではロードしない。</summary>
    public string? SelectedModel { get => _model; set => SetProperty(ref _model, value); }
    /// <summary>現在インストールされているモデル。</summary>
    public ObservableCollection<string> Models { get; } = [];
    /// <summary>最新100件の巡回履歴。</summary>
    public ObservableCollection<WebRunViewModel> Runs { get; } = [];
    /// <summary>最新100件の通知。</summary>
    public ObservableCollection<WebNotificationViewModel> Notifications { get; } = [];
    /// <summary>全体の最新100件から現在の情報源で絞り込んだ通知。DB 全件の検索ではない。</summary>
    public ObservableCollection<WebNotificationViewModel> VisibleNotifications { get; } = [];
    /// <summary>最新100件から現在の情報源で絞り込んだ巡回履歴。</summary>
    public ObservableCollection<WebRunViewModel> VisibleRuns { get; } = [];
    /// <summary>閲覧用のすべてと登録中情報源。設定編集の選択とは独立する。</summary>
    public ObservableCollection<FeedSourceChoice> SourceChoices { get; } = [];
    /// <summary>閲覧中の情報源。定期更新では同じ ID のインスタンスを保つ。</summary>
    public FeedSourceChoice? SelectedFeedSource
    {
        get => _selectedSource;
        set { if (value is not null && !ReferenceEquals(value, _selectedSource)) { EndReadingSelection(); SetProperty(ref _selectedSource, value); ApplyFilter(); OnPropertyChanged(nameof(FeedHeading)); QueryChanged(); } }
    }
    /// <summary>現在の閲覧対象と予定。</summary>
    public string FeedHeading => SelectedFeedSource?.Title ?? "すべての通知";
    /// <summary>絞り込みの対象範囲と空状態を示す。</summary>
    public string RangeText => _notificationManagement is null ? $"全情報源の最新100件から表示: {VisibleNotifications.Count}件 / 取得済み {Notifications.Count}件" :
        !HasUnreadCounts ? "未読件数は未取得です。Worker 接続後に DB 全通知を集計します。" :
        (FilterIndex == 0 && _activeTagName is null ? "全情報源の初回取得日時の最新100件から表示" : $"DB 条件一致 {_matchingCount}件のうち初回取得日時の最新100件") + $": {VisibleNotifications.Count}件 / 未読総数 {TotalUnread}件（全通知・最終集計）";
    /// <summary>空の一覧でも停止・未処理と区別できる案内。</summary>
    public string EmptyText => VisibleNotifications.Count == 0 ? "表示条件に合う通知はありません。情報源・未読／ブックマーク・タグの条件と、Worker・AI の状態を確認してください。" : "";
    /// <summary>Worker のスナップショットを表示へ反映する。削除済み情報源の記事はすべてに残す。</summary>
    public void UpdateSources(System.Collections.Generic.IReadOnlyList<WorkerSourceViewModel> sources)
    {
        var previousUrl = SelectedFeedSource?.Url;
        var choices = sources.Select(source =>
        {
            var existing = SourceChoices.FirstOrDefault(choice => choice.Id == source.Id);
            if (existing is null) return new FeedSourceChoice(source.Id, source.Title, source.Source.Url, source.Schedule);
            existing.Update(source.Title, source.Source.Url, source.Schedule); return existing;
        }).Prepend(SourceChoices[0]).ToArray();
        Synchronize(SourceChoices, choices);
        if (!SourceChoices.Contains(_selectedSource!)) _selectedSource = SourceChoices[0];
        OnPropertyChanged(nameof(SelectedFeedSource)); OnPropertyChanged(nameof(FeedHeading));
        if (previousUrl != SelectedFeedSource?.Url) EndReadingSelection();
        ApplyFilter();
        ApplyUnreadCounts();
        if (previousUrl != SelectedFeedSource?.Url) QueryChanged();
    }
    private void ApplyFilter()
    {
        var url = SelectedFeedSource?.Url;
        foreach (var item in Notifications)
            item.SourceName = SourceChoices.FirstOrDefault(choice => choice.Url is not null && choice.Url == item.Item.FeedSourceUrl)?.Title ?? "削除済み・未登録の情報源";
        Synchronize(VisibleNotifications, Notifications.Where(item => (url is null || item.Item.FeedSourceUrl == url) &&
            (_notificationManagement is null || FilterIndex != 1 || item.IsUnread) &&
            (_notificationManagement is null || FilterIndex != 2 || item.Item.IsBookmarked) &&
            (_notificationManagement is null || _activeTagName is null || item.TagItems.Any(tag => TagNameRules.Normalize(tag.Name).Key == TagNameRules.Normalize(_activeTagName).Key))).ToArray());
        Synchronize(VisibleRuns, Runs.Where(item => url is null || item.Run.Source == url).ToArray());
        if (SelectedNotification is not null && !VisibleNotifications.Contains(SelectedNotification) && SelectedNotification.Item.Notification.Id != _openedArticleId) SelectedNotification = null;
        UpdateDateGroups();
        OnPropertyChanged(nameof(RangeText)); OnPropertyChanged(nameof(EmptyText)); NotifyCommands();
    }
    /// <summary>同じ行を再利用して挿入・移動だけを通知し、選択とスクロールを維持する。</summary>
    private static void Synchronize<T>(ObservableCollection<T> target, System.Collections.Generic.IReadOnlyList<T> desired)
    {
        for (var index = 0; index < desired.Count; index++)
        {
            var old = target.IndexOf(desired[index]);
            if (old < 0) target.Insert(index, desired[index]); else if (old != index) target.Move(old, index);
        }
        while (target.Count > desired.Count) target.RemoveAt(target.Count - 1);
    }
    /// <summary>参照先を開く対象の通知。</summary>
    public WebNotificationViewModel? SelectedNotification
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value)) return;
            if (_selected?.Item.Notification.Id != value?.Item.Notification.Id) FinishReading();
            if (_selected is { } previous) previous.IsCurrent = false;
            if (value is not null) value.IsCurrent = true;
            if (SetProperty(ref _selected, value)) { OnPropertyChanged(nameof(SelectedDocument)); NotifyCommands(); NotifyMetadataCommands(); }
        }
    }
    /// <summary>選択した通知の Markdown 全文。変換・安全化は表示コントロールが担当する。</summary>
    public MessageSnapshot? SelectedDocument => SelectedNotification is { } selected ? new(selected.Item.Notification.Id.ToString("N"),
        selected.Item.Notification.MarkdownBody, selected.Title, selected.Detail, false, true) : null;
    /// <summary>収集処理全体が進行中か。</summary>
    public bool IsRunning { get => _busy; private set { if (SetProperty(ref _busy, value)) { OnPropertyChanged(nameof(CanEdit)); NotifyCommands(); } } }
    /// <summary>モデル処理と競合せず設定や収集開始を操作できるか。</summary>
    public bool CanEdit => !_disposed && !IsRunning && !_refreshing && !_lifecycle.IsBusy && !_lifecycle.IsClosing;
    /// <summary>進捗または完了状態。</summary>
    public string StateText { get => _state; private set => SetProperty(ref _state, value); }
    /// <summary>直近の操作エラー。</summary>
    public string ErrorMessage { get => _error; private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    /// <summary>エラー表示の有無。</summary>
    public bool HasError => ErrorMessage.Length != 0;
    /// <summary>設定を保存して1回収集する。</summary>
    public AsyncCommand FetchCommand { get; }
    /// <summary>接続先と要約モデルだけを保存する。</summary>
    public AsyncCommand SaveSettingsCommand { get; }
    /// <summary>モデル、履歴、通知を再取得する。</summary>
    public AsyncCommand RefreshCommand { get; }
    /// <summary>Worker の結果だけを再取得する。設定の再読込や Ollama 操作は行わない。</summary>
    public AsyncCommand RefreshFeedCommand { get; }
    /// <summary>進行中の収集を中止する。</summary>
    public RelayCommand CancelCommand { get; }
    /// <summary>明示選択した通知の元記事を開く。</summary>
    public AsyncCommand OpenSourceCommand { get; }
    /// <summary>内部 ID に対応する保存 Markdown を開く。</summary>
    public AsyncCommand OpenMarkdownCommand { get; }
    /// <summary>専用の選択ダイアログで1件または複数件を確認して削除する。</summary>
    public AsyncCommand DeleteArticlesCommand { get; }
    /// <summary>表示中の記事から削除対象を選び、確認済み ID を返す。null はキャンセル。</summary>
    public Func<System.Collections.Generic.IReadOnlyList<WebNotificationViewModel>, Task<System.Collections.Generic.IReadOnlyList<Guid>?>>? RequestArticleDeletionAsync { get; set; }
    /// <summary>設定再読み込み後に値を復元する。存在しないモデルは再選択を求める。</summary>
    public void ApplySettings(string? url, string? model)
    {
        if (IsRunning || _disposed) return;
        FeedUrl = url ?? "";
        SelectedModel = model is not null && Models.Contains(model) ? model : null;
        if (model is not null && SelectedModel is null) ErrorMessage = "保存された要約モデルが見つかりません。モデルを再選択してください。";
    }
    /// <summary>初回または手動の再取得。DB 読込の失敗と Ollama の失敗をそれぞれ表示する。</summary>
    public async Task RefreshAsync()
    {
        if (_disposed || IsRunning || _refreshing) return;
        _refreshing = true; OnPropertyChanged(nameof(CanEdit)); NotifyCommands();
        ErrorMessage = "";
        try { await RefreshResultsAsync(); }
        catch (Exception exception) { Failure(exception); }
        try
        {
            var models = await _chat.GetModelsAsync();
            if (_disposed) return;
            var selected = SelectedModel;
            Models.Clear(); foreach (var model in models) Models.Add(model);
            SelectedModel = selected is not null && Models.Contains(selected) ? selected : null;
        }
        catch (Exception exception) { Failure(exception); }
        finally { _refreshing = false; OnPropertyChanged(nameof(CanEdit)); NotifyCommands(); }
    }
    /// <summary>Worker の状態更新から呼ぶ、Ollama に依存しない通知・履歴の更新。</summary>
    public async Task RefreshFeedAsync()
    {
        if (_disposed || IsRunning) return;
        if (_refreshing) { _queryPending = true; if (_refreshIdle is { } pending) await pending.Task; return; }
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _refreshIdle = completed;
        try
        {
            do
            {
                _queryPending = false; _refreshing = true;
                try { await RefreshResultsAsync(); ErrorMessage = ""; }
                catch (Exception exception) { Failure(exception); }
                finally { _refreshing = false; OnPropertyChanged(nameof(CanEdit)); NotifyCommands(); }
            } while (_queryPending && !_disposed && !IsRunning);
        }
        finally { _refreshIdle = null; completed.TrySetResult(); }
    }
    /// <summary>原文を含めず失敗を通知する。</summary>
    private void Failure(Exception exception)
    {
        _diagnostics.Error("web.ui", exception);
        if (!_disposed) ErrorMessage = exception is StorageException or WebMonitoringException or WorkerException ? exception.Message : "操作に失敗しました。Ollama・接続状態・保存先を確認してください。";
    }
    /// <summary>結果の差分を反映し、定期更新で通知の選択・Markdown の読取位置を失わない。破棄後の結果は反映しない。</summary>
    private async Task RefreshResultsAsync()
    {
        var runs = await _store.RunsAsync(CancellationToken.None);
        var revision = _queryRevision;
        var query = new NotificationQuery(SelectedFeedSource?.Url, (NotificationViewFilter)FilterIndex, _activeTagName);
        var page = _notificationManagement is null ? null : await _notificationManagement.QueryAsync(query);
        var notifications = page?.Items ?? await _store.NotificationsAsync(CancellationToken.None);
        if (_disposed || revision != _queryRevision) { _queryPending = !_disposed; return; }
        if (page is not null) AcceptPage(page);
        if (!Runs.Select(item => item.Run).SequenceEqual(runs))
        { Runs.Clear(); foreach (var run in runs) Runs.Add(new(run)); }
        var selected = SelectedNotification?.Item.Notification.Id;
        for (var index = 0; index < notifications.Count; index++)
        {
            var notification = notifications[index];
            var existing = Notifications.FirstOrDefault(item => item.Item.Notification.Id == notification.Notification.Id);
            if (existing is null) Notifications.Insert(index, new(notification));
            else
            {
                var oldIndex = Notifications.IndexOf(existing);
                if (oldIndex != index) Notifications.Move(oldIndex, index);
                var previousDocument = SelectedDocument;
                existing.Update(notification);
                if (ReferenceEquals(existing, SelectedNotification) && previousDocument != SelectedDocument) OnPropertyChanged(nameof(SelectedDocument));
            }
        }
        while (Notifications.Count > notifications.Count) Notifications.RemoveAt(Notifications.Count - 1);
        ApplyFilter();
        var restored = VisibleNotifications.FirstOrDefault(item => item.Item.Notification.Id == selected);
        if (restored is not null || selected != _openedArticleId) SelectedNotification = restored;
        NotifyCommands();
    }
    /// <summary>確認した対象だけを削除し、成功・失敗・中止後の実状態を一覧へ反映する。</summary>
    private async Task DeleteArticlesAsync()
    {
        if (RequestArticleDeletionAsync is null) return;
        IsRunning = true; ErrorMessage = "";
        using var cancellation = new CancellationTokenSource(); _runCancellation = cancellation;
        try
        {
            var visible = VisibleNotifications.ToArray();
            var ids = await RequestArticleDeletionAsync(visible);
            if (ids is null || ids.Count == 0) return;
            if (ids.Any(id => !visible.Any(item => item.Item.Notification.Id == id))) throw new WebMonitoringException("表示中の記事から削除対象を選択してください。");
            await DeleteConfirmedArticlesAsync(ids, cancellation.Token);
        }
        catch (Exception exception) { Failure(exception); }
        finally { _runCancellation = null; IsRunning = false; ResumePendingRead(); }
    }
    /// <summary>検証済みの URL を既存の設定キューに保存する。</summary>
    private async Task SaveConfigurationAsync()
    {
        try { FeedIdentity.ValidateUrl(FeedUrl.Trim()); ErrorMessage = await _settings.RememberWebAsync(FeedUrl.Trim(), SelectedModel) ? "" : "設定を保存できません。設定画面でエラーを確認してください。"; }
        catch (Exception exception) { Failure(exception); }
    }
    /// <summary>非同期ユースケースを起動し、完了・失敗・中止後に必ず保存結果を再表示する。</summary>
    private async Task CollectAsync()
    {
        IsRunning = true; ErrorMessage = "";
        var generation = ++_generation;
        using var cancellation = new CancellationTokenSource(); _runCancellation = cancellation;
        try
        {
            FeedIdentity.ValidateUrl(FeedUrl.Trim());
            // 保存不能でも今回だけの手動実行は可能。既存の不正 JSON は上書きしない。
            if (!await _settings.RememberWebAsync(FeedUrl.Trim(), SelectedModel)) ErrorMessage = "設定を保存できませんでした。今回の入力で収集を実行します。";
            var progress = new Progress<string>(text => { if (!_disposed && IsRunning && generation == _generation) StateText = text; });
            await _service.CollectAsync(FeedUrl.Trim(), SelectedModel, progress, cancellation.Token);
            if (!_disposed)
            {
                await RefreshResultsAsync();
                if (Runs.FirstOrDefault() is { } latest) { StateText = latest.Detail; if (latest.Run.Error.Length != 0) ErrorMessage = latest.Run.Error; }
            }
        }
        catch (Exception exception) { Failure(exception); }
        finally { ++_generation; _runCancellation = null; IsRunning = false; }
    }
    /// <summary>UI の明示操作だけで参照先を開く。</summary>
    private async Task OpenAsync(bool markdown, Func<ArticleNotification, bool, Task> open)
    {
        try { if (SelectedNotification is { } selected) await open(selected.Item, markdown); }
        catch (Exception exception) { Failure(exception); }
    }
    /// <summary>終了時に中止を要求し、履歴の確定を期限内で待つ。待機失敗は終了を妨げない。</summary>
    public async Task StopAsync()
    {
        _runCancellation?.Cancel();
        try { await _running.WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (Exception exception) { _diagnostics.Error("web.close", exception); }
        try { await EndReadingAsync().WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (Exception exception) { _diagnostics.Error("web.read.close", exception); }
        try { if (_refreshIdle is { } refresh) await refresh.Task.WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (Exception exception) { _diagnostics.Error("web.refresh.close", exception); }
    }
    /// <summary>モデル状態の変更を UI スレッドへ移す。</summary>
    private void ModelStateChanged(object? sender, EventArgs args) => _dispatch(() => { if (!_disposed) { OnPropertyChanged(nameof(CanEdit)); NotifyCommands(); } });
    /// <summary>各操作の実行可能状態を再評価する。</summary>
    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanDeleteArticle)); OnPropertyChanged(nameof(CanManageNotification));
        FetchCommand?.NotifyCanExecuteChanged(); SaveSettingsCommand?.NotifyCanExecuteChanged(); RefreshCommand?.NotifyCanExecuteChanged();
        RefreshFeedCommand?.NotifyCanExecuteChanged();
        CancelCommand?.NotifyCanExecuteChanged(); OpenSourceCommand?.NotifyCanExecuteChanged(); OpenMarkdownCommand?.NotifyCanExecuteChanged();
        DeleteArticlesCommand?.NotifyCanExecuteChanged();
    }
    /// <summary>購読と進行中操作を解除する。</summary>
    public void Dispose() { if (_disposed) return; _disposed = true; _runCancellation?.Cancel(); _lifecycle.Changed -= ModelStateChanged; }
}
/// <summary>巡回履歴の読みやすい件数表示。</summary>
public sealed record WebRunViewModel(CollectionRun Run)
{
    /// <summary>日時と終了状態。</summary>
    public string Title => $"{Run.StartedAt.ToLocalTime():g} — {Run.Status}";
    /// <summary>段階別件数と終了時刻。</summary>
    public string Detail => $"{Run.Status} / 取得 {Run.Fetched}・新規 {Run.New}・重複 {Run.Duplicates}・要約成功 {Run.Summarized}・記事失敗 {Run.Failed}" +
        (Run.FinishedAt is { } end ? $" / 終了 {end.ToLocalTime():g}" : "") + $"\n{Run.Source} / {Run.Model ?? "モデル未選択"}";
    /// <summary>失敗または中止の概要。</summary>
    public string Error => Run.Error;
}
/// <summary>共通通知 DTO の原文を保ち、一覧用の短い抜粋を提供する。</summary>
#if WINDOWS
[WinRT.GeneratedBindableCustomProperty]
#endif
public sealed partial class WebNotificationViewModel(ArticleNotification item) : ObservableObject
{
    private bool _isCurrent;
    /// <summary>詳細に表示中の記事。既読状態やキーボードフォーカスとは独立した一覧の強調表示に使う。</summary>
    public bool IsCurrent { get => _isCurrent; internal set => SetProperty(ref _isCurrent, value); }
    private string _sourceName = "";
    /// <summary>記事 URL と取得元を分離した永続通知。</summary>
    public ArticleNotification Item { get; private set; } = item;
    /// <summary>記事の初回取得時刻。通知確定時刻と公開時刻を混同しない。</summary>
    public DateTimeOffset? FetchedAt => Item.FetchedAt;
    /// <summary>通知生成済みの行だけがこの表示モデルになる。</summary>
    public bool IsUnread => Item.ReadAt is null;
    /// <summary>色に依存しない未読表示。</summary>
    public string ReadLabel => IsUnread ? "● 未読" : "既読";
    /// <summary>実際に保存されたブックマーク状態。</summary>
    public string BookmarkLabel => Item.IsBookmarked ? "★ 解除" : "☆ 保存";
    /// <summary>一覧のアイコンだけのブックマーク表示。</summary>
    public string BookmarkIcon => Item.IsBookmarked ? "★" : "☆";
    /// <summary>アイコンのツールチップとアクセシブル名。</summary>
    public string BookmarkActionName => Item.IsBookmarked ? "ブックマークを解除" : "ブックマークに追加";
    /// <summary>独立ラベルを作らず、同じ補助情報行の末尾にだけ付ける。</summary>
    public string UnreadSuffix => IsUnread ? " ・ 未読" : "";
    /// <summary>記事の付与済みタグ。</summary>
    public ObservableCollection<ArticleTag> TagItems { get; } = new(item.Tags ?? []);
    /// <summary>メタデータだけの変更で Markdown 文書を作り直さない。</summary>
    public void Update(ArticleNotification current)
    {
        Item = current;
        var tags = current.Tags ?? [];
        if (!TagItems.SequenceEqual(tags)) { TagItems.Clear(); foreach (var tag in tags) TagItems.Add(tag); }
        foreach (var property in new[] { nameof(Item), nameof(IsUnread), nameof(ReadLabel), nameof(BookmarkLabel), nameof(BookmarkIcon), nameof(BookmarkActionName), nameof(UnreadSuffix), nameof(ListDetail), nameof(Title), nameof(ShortExcerpt), nameof(Detail) }) OnPropertyChanged(property);
    }
    /// <summary>現在の登録名。削除済み情報源も通知自体は維持する。</summary>
    public string SourceName { get => _sourceName; set { if (SetProperty(ref _sourceName, value)) OnPropertyChanged(nameof(ListDetail)); } }
    /// <summary>一覧用の短い情報源名と日時。URL とモデルの詳細は本文側へ置く。</summary>
    public string ListDetail => $"{SourceName} ・ 初回取得 {(Item.FetchedAt is { } fetched ? fetched.ToLocalTime().ToString("g") : "日時不明")}";
    /// <summary>一覧用の短いプレーンテキスト抜粋。</summary>
    public string ShortExcerpt => Item.Notification.MarkdownBody.Length > 100 ? Item.Notification.MarkdownBody[..100] + "…" : Item.Notification.MarkdownBody;
    /// <summary>記事タイトル。</summary>
    public string Title => Item.Notification.Title;
    /// <summary>実行しないプレーンテキストとして表示する要約の抜粋。</summary>
    public string Excerpt => Item.Notification.MarkdownBody.Length > 1000 ? Item.Notification.MarkdownBody[..1000] + "…" : Item.Notification.MarkdownBody;
    /// <summary>公開日時・元 URL・取得方法と、現在のモデル選択とは独立した実際の要約モデル。</summary>
    public string Detail => (Item.FetchedAt is { } fetched ? $"初回取得 {fetched.ToLocalTime():g}\n" : "") + (Item.PublishedAt is { } published ? $"公開 {published.ToLocalTime():g}" :
        $"公開日時不明・通知 {Item.Notification.OccurredAt.ToLocalTime():g}") + $" / {Item.SourceUrl ?? "URL なし"}" +
        $"\n取得: {(Item.GitHubRelease is null ? "RSS / Atom" : "GitHub Releases")} / 要約モデル: {(string.IsNullOrWhiteSpace(Item.SummaryModel) ? "不明（記録なし）" : Item.SummaryModel)}" +
        $"\n取得元: {Item.FeedSourceUrl ?? "不明（記録なし）"}" +
        (Item.GitHubRelease is { } release ? $"\nリポジトリ: {release.Repository} / Release ID: {release.ReleaseId} / リリースタグ: {release.TagName} / {(release.Prerelease ? "プレリリース" : "正式版")}" : "");
}

/// <summary>閲覧用の情報源。更新で同じオブジェクトを使い、選択を失わない。</summary>
#if WINDOWS
[WinRT.GeneratedBindableCustomProperty]
#endif
public sealed partial class FeedSourceChoice(Guid id, string title, string? url, string detail) : ObservableObject
{
    private int _unread;
    private bool _hasUnreadCount;
    /// <summary>DB 全通知から集計した、この情報源の未読数。</summary>
    public int UnreadCount => _unread;
    /// <summary>登録名と未読数を併記する表示。</summary>
    public string DisplayTitle => _hasUnreadCount ? $"{Title}（未読 {_unread}）" : Title;
    /// <summary>件数更新でも選択のオブジェクトを作り直さない。</summary>
    public void SetUnreadCount(int count) { if (_unread != count || !_hasUnreadCount) { _hasUnreadCount = true; _unread = count; OnPropertyChanged(nameof(UnreadCount)); OnPropertyChanged(nameof(DisplayTitle)); } }
    /// <summary>すべては Empty、それ以外は登録情報源の ID。</summary>
    public Guid Id { get; } = id;
    /// <summary>現在の表示名。</summary>
    public string Title { get; private set; } = title;
    /// <summary>記事 URL ではなく取得元フィード URL。</summary>
    public string? Url { get; private set; } = url;
    /// <summary>保存済みの周期と次回予定。</summary>
    public string Detail { get; private set; } = detail;
    /// <summary>登録名や予定のみの変化を変更通知で反映する。</summary>
    public void Update(string name, string? feedUrl, string state)
    {
        if (Title != name) { Title = name; OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(DisplayTitle)); }
        if (Url != feedUrl) { Url = feedUrl; OnPropertyChanged(nameof(Url)); }
        if (Detail != state) { Detail = state; OnPropertyChanged(nameof(Detail)); }
    }
}
