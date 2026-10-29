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

namespace Ateliers.Ai.Assistant.Desktop.ViewModels;

/// <summary>単一フィードの手動実行と永続結果の表示。収集・DB・HTTP の詳細はサービスへ委譲する。</summary>
#if WINDOWS
[WinRT.GeneratedBindableCustomProperty]
#endif
public sealed partial class WebMonitoringViewModel : ObservableObject, IDisposable
{
    private readonly WebCollectionService _service;
    private readonly IWebMonitoringStore _store;
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

    /// <summary>ユースケースと表示用のポートをホストから受け取る。</summary>
    public WebMonitoringViewModel(WebCollectionService service, IWebMonitoringStore store, IChatService chat,
        IModelLifecycleService lifecycle, SettingsViewModel settings, IAppDiagnostics diagnostics, Action<Action> dispatch,
        Func<ArticleNotification, bool, Task> openLink)
    {
        _service = service; _store = store; _chat = chat; _lifecycle = lifecycle; _settings = settings; _diagnostics = diagnostics; _dispatch = dispatch;
        FetchCommand = new(() => _running = CollectAsync(), () => CanEdit && !string.IsNullOrWhiteSpace(FeedUrl));
        SaveSettingsCommand = new(SaveConfigurationAsync, () => CanEdit);
        RefreshCommand = new(RefreshAsync, () => !_disposed && !IsRunning && !_refreshing);
        CancelCommand = new(() => _runCancellation?.Cancel(), () => IsRunning);
        OpenSourceCommand = new(() => OpenAsync(false, openLink), () => SelectedNotification?.Item.SourceUrl is not null);
        OpenMarkdownCommand = new(() => OpenAsync(true, openLink), () => SelectedNotification is not null);
        DeleteArticlesCommand = new(() => _running = DeleteArticlesAsync(), () => CanEdit && Notifications.Count != 0);
        _lifecycle.Changed += ModelStateChanged;
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
    /// <summary>参照先を開く対象の通知。</summary>
    public WebNotificationViewModel? SelectedNotification
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value)) { OnPropertyChanged(nameof(SelectedDocument)); NotifyCommands(); } }
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
    /// <summary>原文を含めず失敗を通知する。</summary>
    private void Failure(Exception exception)
    {
        _diagnostics.Error("web.ui", exception);
        if (!_disposed) ErrorMessage = exception is StorageException or WebMonitoringException ? exception.Message : "操作に失敗しました。Ollama・接続状態・保存先を確認してください。";
    }
    /// <summary>結果のスナップショットで一覧を入れ替える。遅い結果は破棄後に反映しない。</summary>
    private async Task RefreshResultsAsync()
    {
        var runs = await _store.RunsAsync(CancellationToken.None);
        var notifications = await _store.NotificationsAsync(CancellationToken.None);
        if (_disposed) return;
        Runs.Clear(); foreach (var run in runs) Runs.Add(new(run));
        var selected = SelectedNotification?.Item.Notification.Id;
        Notifications.Clear(); foreach (var notification in notifications) Notifications.Add(new(notification));
        SelectedNotification = Notifications.FirstOrDefault(item => item.Item.Notification.Id == selected);
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
            var visible = Notifications.ToArray();
            var ids = await RequestArticleDeletionAsync(visible);
            if (ids is null || ids.Count == 0) return;
            if (ids.Any(id => !visible.Any(item => item.Item.Notification.Id == id))) throw new WebMonitoringException("表示中の記事から削除対象を選択してください。");
            StateText = $"{ids.Distinct().Count()}件の記事を削除しています…";
            var result = await _service.DeleteAsync(ids, cancellation.Token);
            await RefreshResultsAsync();
            if (!_disposed)
            {
                StateText = $"削除 {result.DeletedIds.Count}件・失敗 {result.FailedIds.Count}件" + (result.Canceled ? "（中止）" : "");
                ErrorMessage = result.Error;
            }
        }
        catch (Exception exception) { Failure(exception); }
        finally { _runCancellation = null; IsRunning = false; }
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
    }
    /// <summary>モデル状態の変更を UI スレッドへ移す。</summary>
    private void ModelStateChanged(object? sender, EventArgs args) => _dispatch(() => { if (!_disposed) { OnPropertyChanged(nameof(CanEdit)); NotifyCommands(); } });
    /// <summary>各操作の実行可能状態を再評価する。</summary>
    private void NotifyCommands()
    {
        FetchCommand?.NotifyCanExecuteChanged(); SaveSettingsCommand?.NotifyCanExecuteChanged(); RefreshCommand?.NotifyCanExecuteChanged();
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
public sealed record WebNotificationViewModel(ArticleNotification Item)
{
    /// <summary>記事タイトル。</summary>
    public string Title => Item.Notification.Title;
    /// <summary>実行しないプレーンテキストとして表示する要約の抜粋。</summary>
    public string Excerpt => Item.Notification.MarkdownBody.Length > 1000 ? Item.Notification.MarkdownBody[..1000] + "…" : Item.Notification.MarkdownBody;
    /// <summary>公開日時・元 URL・取得方法と、現在のモデル選択とは独立した実際の要約モデル。</summary>
    public string Detail => (Item.PublishedAt is { } published ? $"公開 {published.ToLocalTime():g}" :
        $"公開日時不明・通知 {Item.Notification.OccurredAt.ToLocalTime():g}") + $" / {Item.SourceUrl ?? "URL なし"}" +
        $"\n取得: RSS / Atom / 要約モデル: {(string.IsNullOrWhiteSpace(Item.SummaryModel) ? "不明（記録なし）" : Item.SummaryModel)}";
}
