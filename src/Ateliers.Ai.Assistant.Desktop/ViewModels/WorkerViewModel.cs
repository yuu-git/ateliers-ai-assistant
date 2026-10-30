using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ateliers.Ai.Assistant.Application.Chat;
using Ateliers.Ai.Assistant.Application.Storage;
using Ateliers.Ai.Assistant.Application.Worker;
using Ateliers.Ai.Assistant.Application.WebMonitoring;

namespace Ateliers.Ai.Assistant.Desktop.ViewModels;

/// <summary>情報源 CRUD、Worker の運転設定・予定・接続状態。収集パイプラインは実装しない。</summary>
#if WINDOWS
[WinRT.GeneratedBindableCustomProperty]
#endif
public sealed partial class WorkerViewModel : ObservableObject, IDisposable
{
    private readonly IWorkerClient _client;
    private readonly IWorkerLauncher _launcher;
    private readonly IChatService _models;
    private readonly IAppDiagnostics _diagnostics;
    private readonly CancellationTokenSource _lifetime = new();
    private WorkerConfiguration _configuration = WorkerConfiguration.Default;
    private WorkerSourceViewModel? _selected;
    private string _name = "", _url = "", _model = "", _connection = "未接続", _error = "", _startup = "登録状態を確認してください。", _ai = "不明";
    private int _interval = 60, _pending;
    private bool _automatic, _paused, _load, _unload, _busy, _connected, _polling, _disposed, _loadedSettings;
    private long _refreshRevision;
    private string _saveMessage = "";
    private bool _applyingAi, _aiEdited;
    private bool _rssFetching;
    /// <summary>閲覧用の情報源一覧を更新する。設定編集の選択は共有しない。</summary>
    public Action<System.Collections.Generic.IReadOnlyList<WorkerSourceViewModel>>? SourcesUpdated { get; set; }
    /// <summary>明示再読込で編集中の値を破棄する前の確認。</summary>
    public Func<Task<bool>>? ConfirmDiscardEditsAsync { get; set; }
    /// <summary>操作の完了後に通知・履歴を更新するための依頼。</summary>
    public Func<Task>? RefreshResultsAsync { get; set; }
    /// <summary>情報源の削除を確認する。記事は削除しない。</summary>
    public Func<string, Task<bool>>? ConfirmDeleteSourceAsync { get; set; }
    /// <summary>通信・起動・モデル一覧の依存を受け取る。</summary>
    public WorkerViewModel(IWorkerClient client, IWorkerLauncher launcher, IChatService models, IAppDiagnostics diagnostics, TimeProvider? clock = null)
    {
        _client = client; _launcher = launcher; _models = models; _diagnostics = diagnostics;
        _runtimeClock = clock ?? TimeProvider.System;
        RefreshCommand = new(RefreshAsync, () => !_busy);
        StartCommand = new(() => ActionAsync(StartWorkerAsync), () => CanStartWorker);
        StopCommand = new(() => ActionAsync(StopWorkerAsync), () => CanStopWorker);
        NewSourceCommand = new(() => { SelectedSource = null; Name = ""; Url = ""; IntervalMinutes = 60; Automatic = false; KindIndex = 0; IncludePrereleases = false; }, () => !_busy);
        SaveSourceCommand = new(() => ActionAsync(SaveSourceAsync), () => Connected && !_busy);
        DeleteSourceCommand = new(() => ActionAsync(DeleteSourceAsync), () => Connected && !_busy && SelectedSource is not null);
        FetchCommand = new(() => ActionAsync(async () => { if (SelectedSource is { } source) await _client.FetchAsync(new(source.Id, Guid.NewGuid()), _lifetime.Token); await RefreshCoreAsync(false); }), () => Connected && !_busy && SelectedSource is not null);
        SaveOptionsCommand = new(() => ActionAsync(SaveAiSettingsAsync), () => Connected && !_busy);
        EnableStartupCommand = new(() => ActionAsync(async () => StartupText = (await _launcher.SetStartupAsync(true)).Description), () => !_busy);
        DisableStartupCommand = new(() => ActionAsync(async () => StartupText = (await _launcher.SetStartupAsync(false)).Description), () => !_busy);
        ToggleAiPauseCommand = new(() => ActionAsync(ToggleAiPauseAsync), () => CanToggleActualAi);
    }
    /// <summary>登録されている稼働中の情報源。</summary>
    public ObservableCollection<WorkerSourceViewModel> Sources { get; } = [];
    /// <summary>Ollama で取得した要約モデル候補。</summary>
    public ObservableCollection<string> Models { get; } = [];
    /// <summary>編集・手動取得の対象。</summary>
    public WorkerSourceViewModel? SelectedSource { get => _selected; set { if (SetProperty(ref _selected, value)) { if (value is not null) { ApplySource(value.Source); } CommandsChanged(); } } }
    /// <summary>情報源名。</summary>
    public string Name { get => _name; set { if (SetProperty(ref _name, value)) EditStateChanged(); } }
    /// <summary>HTTP(S) フィード URL。</summary>
    public string Url { get => _url; set { if (SetProperty(ref _url, value)) EditStateChanged(); } }
    /// <summary>変更可能な巡回周期。5分〜1週間。</summary>
    public int IntervalMinutes { get => _interval; set { if (SetProperty(ref _interval, value)) EditStateChanged(); } }
    /// <summary>情報源ごとの自動取得。</summary>
    public bool Automatic { get => _automatic; set { if (SetProperty(ref _automatic, value)) EditStateChanged(); } }
    /// <summary>チャットと独立した要約モデル。未起動時も手入力できる。</summary>
    public string Model { get => _model; set { if (SetProperty(ref _model, value)) AiEdited(); } }
    /// <summary>実行中の要約もキャンセルするグローバル一時停止。</summary>
    public bool AiPaused { get => _paused; set { if (SetProperty(ref _paused, value)) AiEdited(); } }
    /// <summary>Worker の暗黙ロードを含むロード許可。</summary>
    public bool AllowLoad { get => _load; set { if (SetProperty(ref _load, value)) AiEdited(); } }
    /// <summary>Worker が新規ロードを確認したモデルだけの明示解放許可。</summary>
    public bool AllowUnload { get => _unload; set { if (SetProperty(ref _unload, value)) AiEdited(); } }
    /// <summary>AI の編集値と最後に適用した保存値が異なるか。</summary>
    public bool HasAiEdits => !_loadedSettings ? _aiEdited : Model != (_configuration.SummaryModel ?? "") || AllowLoad != _configuration.AllowLoad || AllowUnload != _configuration.AllowUnload || AiPaused != _configuration.AiPaused;
    /// <summary>情報源の編集値が未保存か。</summary>
    public bool HasSourceEdits => SelectedSource is { } source ?
        Name != source.Source.Name || Url != SourceInput(source.Source) || IntervalMinutes != source.Source.IntervalMinutes || Automatic != source.Source.Automatic || KindIndex != (int)source.Source.Kind || IncludePrereleases != source.Source.IncludePrereleases :
        Name.Length != 0 || Url.Length != 0 || Automatic || IntervalMinutes != 60 || KindIndex != 0 || IncludePrereleases;
    /// <summary>保存単位と編集中の状態。</summary>
    public string AiEditState => HasAiEdits ? "未保存の変更があります。AI・モデル設定の保存で反映します。" : _saveMessage.Length != 0 ? _saveMessage : "保存済みの AI・モデル設定です。";
    /// <summary>情報源設定の保存状態。</summary>
    public string SourceEditState => HasSourceEdits ? "情報源の変更は未保存です。保存で反映します。" : SelectedSource is null ? "情報源を選択するか、新しい情報源を入力してください。" : "情報源の保存済み設定を表示しています。";
    /// <summary>最終疎通とバージョンを含む接続状態。</summary>
    public string ConnectionText { get => _connection; private set => SetProperty(ref _connection, value); }
    /// <summary>AI の状態と、取得とは独立した待機理由。</summary>
    public string AiText { get => _ai; private set { if (SetProperty(ref _ai, value)) OnPropertyChanged(nameof(StatusSummary)); } }
    /// <summary>保存済みの未処理記事数。</summary>
    public int PendingCount { get => _pending; private set { if (SetProperty(ref _pending, value)) OnPropertyChanged(nameof(StatusSummary)); } }
    /// <summary>閲覧画面に常時表示する接続と AI・待機数の短い概要。</summary>
    public string StatusSummary => $"Worker: {(Connected ? "接続済み" : "未接続（接続の詳細・復旧から開始できます）")} / 取得: {(_rssFetching ? "巡回中" : "待機中")} / AI: {AiText.Split(" / ")[0]} / 要約待ち {PendingCount}件（失敗・処理中を含む）";
    /// <summary>サインイン時起動の登録・解除の結果。</summary>
    public string StartupText { get => _startup; private set => SetProperty(ref _startup, value); }
    /// <summary>利用者に示す接続・入力・操作エラー。</summary>
    public string ErrorMessage { get => _error; private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    /// <summary>エラーがあるか。</summary>
    public bool HasError => ErrorMessage.Length != 0;
    /// <summary>最後の疎通が成功したか。</summary>
    public bool Connected { get => _connected; private set { if (SetProperty(ref _connected, value)) { OnPropertyChanged(nameof(StatusSummary)); CommandsChanged(); } } }
    /// <summary>設定編集中は自動読込で入力を上書きしないため、自動ポーリングは状態だけに限定する。</summary>
    public AsyncCommand RefreshCommand { get; }
    /// <summary>独立 Worker を開始する。</summary>
    public AsyncCommand StartCommand { get; }
    /// <summary>ウィンドウを閉じる操作とは別に、Worker の停止を要求する。</summary>
    public AsyncCommand StopCommand { get; }
    /// <summary>新規情報源の編集を開始する。</summary>
    public RelayCommand NewSourceCommand { get; }
    /// <summary>名称・周期・自動取得を保存する。</summary>
    public AsyncCommand SaveSourceCommand { get; }
    /// <summary>情報源を停止・削除する。既存記事は保持する。</summary>
    public AsyncCommand DeleteSourceCommand { get; }
    /// <summary>選択した情報源を1回取得する。</summary>
    public AsyncCommand FetchCommand { get; }
    /// <summary>閲覧中の情報源を巡回する。設定編集の選択・入力は変更しない。</summary>
    public Task FetchSourceAsync(Guid id) => ActionAsync(async () =>
    {
        if (!Connected) throw new WorkerException("Worker が停止中です。設定画面から開始してください。");
        await _client.FetchAsync(new(id, Guid.NewGuid()), _lifetime.Token);
        await RefreshCoreAsync(false);
    });
    /// <summary>AI・モデルの運転設定を保存する。</summary>
    public AsyncCommand SaveOptionsCommand { get; }
    /// <summary>利用者が明示的に自動起動を有効化する。</summary>
    public AsyncCommand EnableStartupCommand { get; }
    /// <summary>自動起動を解除する。</summary>
    public AsyncCommand DisableStartupCommand { get; }
    /// <summary>最初の表示でモデル・起動登録・接続状態を取得する。</summary>
    public async Task InitializeAsync()
    {
        await ActionAsync(() => RefreshCoreAsync(false));
        try { StartupText = (await _launcher.GetStartupAsync()).Description; }
        catch (Exception exception) { Failure(exception); }
        try { var models = await _models.GetModelsAsync(_lifetime.Token); Models.Clear(); foreach (var model in models) Models.Add(model); }
        catch (Exception exception) { _diagnostics.Error("worker.models", exception); }
    }
    /// <summary>手動更新では設定の編集値も再読み込みする。</summary>
    public Task RefreshAsync() => ActionAsync(async () =>
    {
        if ((HasAiEdits || HasSourceEdits) && ConfirmDiscardEditsAsync is not null && !await ConfirmDiscardEditsAsync()) return;
        if (ConfirmDiscardEditsAsync is not null) _aiEdited = false;
        await RefreshCoreAsync(true);
    });
    /// <summary>表示中の状態・次回予定だけを定期更新する。編集値と既存の選択は維持する。</summary>
    public async Task PollAsync()
    {
        if (_busy || _polling || _disposed) return;
        _polling = true;
        try { await RefreshCoreAsync(false); }
        catch (Exception exception) { Failure(exception); await ReconcilePresenceAsync(exception); }
        finally { _polling = false; }
    }
    private async Task RefreshCoreAsync(bool applySettings)
    {
        var revision = ++_refreshRevision;
        var status = await _client.GetStatusAsync(_lifetime.Token);
        if (_disposed || revision != _refreshRevision) return;
        // 監視画面だけの設定保存では、編集中の Web / AI 入力を変えず基準版を追従する。
        if (_loadedSettings && _configuration.SummaryModel == status.Configuration.SummaryModel &&
            _configuration.AllowLoad == status.Configuration.AllowLoad && _configuration.AllowUnload == status.Configuration.AllowUnload &&
            _configuration.AiPaused == status.Configuration.AiPaused && _configuration.Sources.SequenceEqual(status.Configuration.Sources))
            _configuration = _configuration with { Version = status.Configuration.Version, ScreenMonitor = status.Configuration.ScreenMonitor, ScreenMonitors = status.Configuration.ScreenMonitors };
        var preserveInitialAiDraft = !_loadedSettings && _aiEdited;
        if (!_loadedSettings) applySettings = true;
        if (_loadedSettings && _configuration.AiPaused != status.Configuration.AiPaused)
        {
            var unchangedPauseDraft = AiPaused == _configuration.AiPaused;
            _configuration = _configuration with { AiPaused = status.Configuration.AiPaused };
            if (unchangedPauseDraft) { _applyingAi = true; try { AiPaused = status.Configuration.AiPaused; } finally { _applyingAi = false; } }
        }
        Connected = true; ErrorMessage = "";
        AcceptRuntimeStatus(status);
        ConnectionText = $"接続済み / v{status.Version} / 最終疎通 {DateTimeOffset.Now:g} / 起動 {status.StartedAt.ToLocalTime():g}";
        AiText = status.AiState + (status.LastError.Length == 0 ? "" : " / " + status.LastError); PendingCount = status.PendingArticles;
        _rssFetching = status.Fetching; OnPropertyChanged(nameof(StatusSummary));
        var incoming = status.Configuration.Sources.Where(source => !source.Deleted).ToArray();
        for (var index = 0; index < incoming.Length; index++)
        {
            var source = incoming[index]; var state = status.Sources.FirstOrDefault(state => state.Id == source.Id);
            var existing = Sources.FirstOrDefault(item => item.Id == source.Id);
            if (existing is null) Sources.Insert(index, new(source, state));
            else { var oldIndex = Sources.IndexOf(existing); if (oldIndex != index) Sources.Move(oldIndex, index); existing.Update(source, state); }
        }
        while (Sources.Count > incoming.Length) Sources.RemoveAt(Sources.Count - 1);
        if (_selected is not null && !Sources.Contains(_selected)) { _selected = null; OnPropertyChanged(nameof(SelectedSource)); }
        CommandsChanged(); SourcesUpdated?.Invoke(Sources.ToArray());
        if (applySettings)
        {
            _configuration = status.Configuration; _loadedSettings = true;
            if (!preserveInitialAiDraft) ApplyAiSettings();
            if (_selected is not null) { ApplySource(_selected.Source); }
        }
        EditStateChanged();
        if (RefreshResultsAsync is not null) await RefreshResultsAsync();
    }
    private async Task SaveSourceAsync()
    {
        var draft = (Name, Url, IntervalMinutes, Automatic, KindIndex, IncludePrereleases);
        var selection = SelectedSource?.Id;
        var url = ContentSourceIdentity.Normalize((ContentSourceKind)KindIndex, Url.Trim());
        var existing = _configuration.Sources.FirstOrDefault(source => source.Url == url);
        if (existing is not null && existing.Id != SelectedSource?.Id) throw new WorkerException("この URL は登録済みです。既存の情報源を選択して編集してください。");
        var id = SelectedSource?.Id ?? Guid.NewGuid();
        var source = new SourceConfiguration(id, Name, url, IntervalMinutes, Automatic, Kind: (ContentSourceKind)KindIndex, IncludePrereleases: IsGitHubSource && IncludePrereleases);
        var sources = _configuration.Sources.Where(item => item.Id != id).Append(source).ToArray();
        await SaveAsync(_configuration with { Sources = sources });
        if ((Name, Url, IntervalMinutes, Automatic, KindIndex, IncludePrereleases) != draft || SelectedSource?.Id != selection)
        {
            if (SelectedSource?.Id == selection) { _selected = Sources.FirstOrDefault(item => item.Id == id); OnPropertyChanged(nameof(SelectedSource)); }
            EditStateChanged(); return;
        }
        SelectedSource = Sources.FirstOrDefault(item => item.Id == id);
        if (SelectedSource is { } saved) { ApplySource(saved.Source); }
        EditStateChanged();
    }
    /// <summary>保存中に続けて入力した新しい編集値を、古い保存完了で上書きしない。</summary>
    private async Task SaveAiSettingsAsync()
    {
        var draft = (Model, AllowLoad, AllowUnload, AiPaused);
        await SaveAsync(_configuration with { SummaryModel = Model, AllowLoad = AllowLoad, AllowUnload = AllowUnload, AiPaused = AiPaused });
        if ((Model, AllowLoad, AllowUnload, AiPaused) == draft) ApplyAiSettings();
        _saveMessage = "AI・モデル設定を保存しました。"; EditStateChanged();
    }
    private async Task DeleteSourceAsync()
    {
        if (SelectedSource is not { } selected || ConfirmDeleteSourceAsync is null || !await ConfirmDeleteSourceAsync(selected.Source.Name)) return;
        await SaveAsync(_configuration with { Sources = _configuration.Sources.Select(source => source.Id == selected.Id ? source with { Deleted = true, Automatic = false } : source).ToArray() });
        SelectedSource = null;
        Name = ""; Url = ""; IntervalMinutes = 60; Automatic = false; KindIndex = 0; IncludePrereleases = false; EditStateChanged();
    }
    private async Task SaveAsync(WorkerConfiguration next)
    {
        ++_refreshRevision;
        _configuration = await _client.SaveConfigurationAsync(next, _lifetime.Token);
        await RefreshCoreAsync(false); EditStateChanged();
    }
    private void ApplyAiSettings()
    {
        _applyingAi = true;
        try { Model = _configuration.SummaryModel ?? ""; AllowLoad = _configuration.AllowLoad; AllowUnload = _configuration.AllowUnload; AiPaused = _configuration.AiPaused; }
        finally { _applyingAi = false; _aiEdited = false; EditStateChanged(); }
    }
    private void AiEdited() { if (!_applyingAi) { _aiEdited = true; _saveMessage = ""; } EditStateChanged(); }
    private void EditStateChanged()
    { OnPropertyChanged(nameof(HasAiEdits)); OnPropertyChanged(nameof(HasSourceEdits)); OnPropertyChanged(nameof(AiEditState)); OnPropertyChanged(nameof(SourceEditState)); }
    private async Task ActionAsync(Func<Task> action)
    {
        if (_disposed || _busy) return; _busy = true; CommandsChanged();
        try { await action(); }
        catch (Exception exception) { Failure(exception); await ReconcilePresenceAsync(exception); }
        finally { _busy = false; CommandsChanged(); }
    }
    private void Failure(Exception exception)
    {
        _diagnostics.Error("worker.ui", exception);
        if (_disposed) return;
        if (exception is WorkerException { ConnectionFailure: true }) { Connected = false; ConnectionText = "接続できません（保存済みログは確認できます）"; }
        ErrorMessage = exception is WorkerException or StorageException or WebMonitoringException ? exception.Message : "Worker の操作に失敗しました。入力値とログを確認してください。";
    }
    private void CommandsChanged()
    {
        NotifyRuntimeState();
        RefreshCommand?.NotifyCanExecuteChanged(); StartCommand?.NotifyCanExecuteChanged(); StopCommand?.NotifyCanExecuteChanged(); NewSourceCommand?.NotifyCanExecuteChanged();
        SaveSourceCommand?.NotifyCanExecuteChanged(); DeleteSourceCommand?.NotifyCanExecuteChanged(); FetchCommand?.NotifyCanExecuteChanged();
        SaveOptionsCommand?.NotifyCanExecuteChanged(); EnableStartupCommand?.NotifyCanExecuteChanged(); DisableStartupCommand?.NotifyCanExecuteChanged();
    }
    /// <summary>UI の購読・通信を止める。Worker の停止は要求しない。</summary>
    public void Dispose() { _disposed = true; _lifetime.Cancel(); _lifetime.Dispose(); }
}
/// <summary>情報源ごとの最終巡回・次回予定・待機理由。</summary>
public sealed class WorkerSourceViewModel(SourceConfiguration source, SourceState? state) : ObservableObject
{
    /// <summary>保存済みの情報源。編集中の入力とは別に保持する。</summary>
    public SourceConfiguration Source { get; private set; } = source;
    /// <summary>直近の予定と実行状態。</summary>
    public SourceState? State { get; private set; } = state;
    /// <summary>同じ行の設定と予定を通知し、選択を維持する。</summary>
    public void Update(SourceConfiguration configuration, SourceState? status)
    { Source = configuration; State = status; OnPropertyChanged(nameof(Source)); OnPropertyChanged(nameof(State)); OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(Detail)); OnPropertyChanged(nameof(Schedule)); }
    /// <summary>操作対象 ID。</summary>
    public Guid Id => Source.Id;
    /// <summary>表示名。</summary>
    public string Title => Source.Name;
    /// <summary>自動取得、周期、最終巡回、次回予定と結果。</summary>
    public string Detail => (Source.Kind == ContentSourceKind.GitHubReleases ? "GitHub Releases / " + (Source.IncludePrereleases ? "プレリリースを含む / " : "正式版のみ / ") : "RSS / Atom / ") + Source.Url + "\n" + Schedule;
    /// <summary>通知画面用の周期・次回予定。長い URL は含めない。</summary>
    public string Schedule => $"自動 {(Source.Automatic ? "ON" : "OFF")} / {Source.IntervalMinutes}分 / 最終 {State?.LastFetchedAt?.ToLocalTime().ToString("g") ?? "未実行"}" +
        $"\n次回 {State?.NextDueAt?.ToLocalTime().ToString("g") ?? "予定なし"} / {State?.Status ?? "未実行"} {State?.Error}";
}
