using System.Collections.Concurrent;
using Ateliers.Ai.Assistant.Application.Chat;
using Ateliers.Ai.Assistant.Application.Logging;
using Ateliers.Ai.Assistant.Application.Models;
using Ateliers.Ai.Assistant.Application.Storage;
using Ateliers.Ai.Assistant.Application.WebMonitoring;

namespace Ateliers.Ai.Assistant.Application.Worker;

/// <summary>取得と要約を独立して進める常駐ユースケース。ホスト・HTTP・UI の具体実装に依存しない。</summary>
public sealed class WorkerCoordinator(IWorkerStateStore settings, IBackgroundArticleStore articles, IContentSource feed,
    IArticleMarkdownWriter writer, IChatService chat, IModelControlService models, IModelUseGate modelGate,
    IAppLog log, IAppDiagnostics diagnostics, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _configurationGate = new(1, 1), _fetchGate = new(1, 1), _aiGate = new(1, 1), _mutationGate = new(1, 1), _stateGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, SourceState> _states = new();
    private readonly ConcurrentQueue<(Guid SourceId, Guid RunId)> _requests = new();
    private volatile WorkerConfiguration _configuration = WorkerConfiguration.Default;
    private volatile CancellationTokenSource? _aiCancellation;
    private volatile bool _fetching, _summarizing;
    private bool _recoverArticles;
    private string _aiState = "起動中", _lastError = "";
    private long _nextAiCheckTicks;
    private long _githubRetryTicks;
    /// <summary>プロセス起動を識別する ID。UI の接続先更新に利用する。</summary>
    public Guid InstanceId { get; } = Guid.NewGuid();
    /// <summary>起動日時。</summary>
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    /// <summary>API 契約版。不一致のクライアントによる書込を拒否する。</summary>
    public const int Protocol = 1;
    /// <summary>ホストと UI が表示する実装版。</summary>
    public const string ImplementationVersion = "5.0.0";

    /// <summary>設定・予定を読み、強制終了時の処理中記事と取得要求を回復する。</summary>
    public async Task InitializeAsync(CancellationToken token)
    {
        _configuration = Validate(await settings.LoadConfigurationAsync(token).ConfigureAwait(false));
        foreach (var state in await settings.LoadStatesAsync(token).ConfigureAwait(false)) _states[state.Id] = state;
        Interlocked.Exchange(ref _githubRetryTicks, _states.Values.Select(state => state.GitHubRetryAt?.UtcTicks ?? 0).DefaultIfEmpty().Max());
        await articles.RecoverAsync(token).ConfigureAwait(false);
        foreach (var source in _configuration.Sources.Where(source => !source.Deleted))
        {
            var state = _states.GetOrAdd(source.Id, id => new(id, NextDueAt: source.Automatic ? _time.GetUtcNow() : null));
            if (state.ActiveRunId is { } run) _requests.Enqueue((source.Id, run));
            await settings.SaveStateAsync(state, token).ConfigureAwait(false);
        }
        _aiState = _configuration.AiPaused ? "一時停止中" : "待機中";
        Record("worker.started", "Worker を起動し、保存済みの予定と待機記事を復元しました。");
    }
    /// <summary>全体状態。未処理件数には失敗・処理中を含める。</summary>
    public async Task<WorkerStatus> StatusAsync(CancellationToken token)
    {
        var count = await articles.PendingCountAsync(_configuration.Sources.Where(source => !source.Deleted).Select(source => source.Url).ToArray(), token).ConfigureAwait(false);
        var visibleStates = _states.Values.Where(state => _configuration.Sources.Any(source => source.Id == state.Id && !source.Deleted))
            .Select(state => state.NextDueAt is { } due && _configuration.Sources.Any(source => source.Id == state.Id && source.Kind == ContentSourceKind.GitHubReleases)
                ? state with { NextDueAt = Later(due, GitHubWaitUntil) } : state).ToArray();
        return new(Protocol, ImplementationVersion, InstanceId, StartedAt, _configuration, visibleStates, count,
            _configuration.AiPaused ? "一時停止中（情報取得は継続）" : _aiState, _lastError, _fetching, _summarizing);
    }
    /// <summary>設定変更を保存し、周期変更を次回予定へ即時反映する。</summary>
    public Task<WorkerConfiguration> ConfigureAsync(WorkerConfiguration requested, CancellationToken token) => UpdateConfigurationAsync(requested, null, token);
    /// <summary>保存済みの他設定を維持して AI の一時停止だけを変更する。</summary>
    public Task<WorkerConfiguration> SetAiPausedAsync(bool paused, CancellationToken token) => UpdateConfigurationAsync(null, paused, token);
    private async Task<WorkerConfiguration> UpdateConfigurationAsync(WorkerConfiguration? requested, bool? paused, CancellationToken token)
    {
        await _configurationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            requested = Validate(requested ?? (_configuration with { AiPaused = paused ?? _configuration.AiPaused }));
            var previous = _configuration;
            _configuration = await settings.SaveConfigurationAsync(requested with { Sources = requested.Sources.Where(source => !source.Deleted).ToArray() }, token).ConfigureAwait(false);
            Interlocked.Exchange(ref _nextAiCheckTicks, 0);
            if (_configuration.AiPaused || previous.SummaryModel != requested.SummaryModel ||
                previous.Sources.Any(old => !requested.Sources.Any(source => source.Id == old.Id && !source.Deleted && source.Url == old.Url)))
                CancelAi();
            foreach (var source in _configuration.Sources)
            {
                var old = previous.Sources.FirstOrDefault(old => old.Id == source.Id);
                if (old is null || old.IntervalMinutes != source.IntervalMinutes || old.Automatic != source.Automatic || old.Url != source.Url || old.Kind != source.Kind || old.IncludePrereleases != source.IncludePrereleases || old.Deleted != source.Deleted)
                {
                    var state = _states.GetOrAdd(source.Id, id => new(id));
                    state = state with { NextDueAt = NextDue(source),
                        Status = state.Status == "情報源削除" ? "再登録（履歴保持）" : state.Status };
                    await SetStateAsync(state, token).ConfigureAwait(false);
                }
            }
            foreach (var removed in previous.Sources.Where(old => !_configuration.Sources.Any(source => source.Id == old.Id)))
            {
                if (_states.TryGetValue(removed.Id, out var state)) await SetStateAsync(state with { NextDueAt = null, Status = "情報源削除" }, token).ConfigureAwait(false);
            }
            var added = _configuration.Sources.Count(source => !previous.Sources.Any(old => old.Id == source.Id));
            var deleted = previous.Sources.Count(old => !_configuration.Sources.Any(source => source.Id == old.Id));
            Record("sources.updated", $"情報源を保存しました。追加 {added}件・削除 {deleted}件・有効 {_configuration.Sources.Count}件。");
            Record("worker.configuration", _configuration.AiPaused ? "設定を保存しました。AI を一時停止し、実行中の要約を中止します。情報取得は継続します。" : "設定を保存しました。巡回周期と次回予定を更新しました。");
            return _configuration;
        }
        finally { _configurationGate.Release(); }
    }
    /// <summary>保存済み実行 ID で要求の再送を判別し、情報源の二重実行を防ぐ。</summary>
    public async Task<FetchAccepted> RequestFetchAsync(FetchRequest request, CancellationToken token)
    {
        if (request.RequestId == Guid.Empty) throw new WorkerException("実行要求 ID が不正です。");
        await _configurationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var source = _configuration.Sources.SingleOrDefault(source => source.Id == request.SourceId && !source.Deleted)
                ?? throw new WorkerException("情報源が見つかりません。設定を更新してください。");
            var existing = await articles.FindRunAsync(request.RequestId, token).ConfigureAwait(false);
            if (existing is not null && existing.Source != source.Url) throw new WorkerException("別の情報源で使われた実行要求 ID です。");
            if (existing?.FinishedAt is not null) return new(existing.Id, true);
            var state = _states.GetOrAdd(source.Id, id => new(id));
            if (state.ActiveRunId is { } active) return new(active, true);
            if (source.Kind == ContentSourceKind.GitHubReleases && GitHubWaitUntil is { } wait)
                throw new WorkerException($"GitHub API の取得を待機中です。解除予定 {wait.ToLocalTime():g} 以降に実行してください。");
            var run = existing ?? new CollectionRun(request.RequestId, _time.GetUtcNow(), Status: "取得待機", Source: source.Url, Model: _configuration.SummaryModel);
            await articles.SaveRunAsync(run, token).ConfigureAwait(false);
            await SetStateAsync(state with { ActiveRunId = run.Id, Status = "取得待機", Error = "" }, token).ConfigureAwait(false);
            _requests.Enqueue((source.Id, run.Id));
            Record("fetch.request", "情報源の取得を受け付けました。", run.Id);
            return new(run.Id, false);
        }
        finally { _configurationGate.Release(); }
    }
    /// <summary>取得ループの1ステップ。AI の状態とは独立し、各情報源は逐次実行する。</summary>
    public async Task TickFetchAsync(CancellationToken token)
    {
        if (!await _fetchGate.WaitAsync(0, token).ConfigureAwait(false)) return;
        try
        {
            if (_requests.IsEmpty)
            {
                var due = _configuration.Sources.FirstOrDefault(source => !source.Deleted && source.Automatic && (source.Kind != ContentSourceKind.GitHubReleases || GitHubWaitUntil is null) &&
                    (!_states.TryGetValue(source.Id, out var state) || state.ActiveRunId is null && (state.NextDueAt ?? DateTimeOffset.MinValue) <= _time.GetUtcNow()));
                if (due is not null) await RequestFetchAsync(new(due.Id, Guid.NewGuid()), token).ConfigureAwait(false);
            }
            if (!_requests.TryDequeue(out var request)) return;
            var source = _configuration.Sources.FirstOrDefault(source => source.Id == request.SourceId && !source.Deleted);
            CollectionRun? run;
            try { run = await articles.FindRunAsync(request.RunId, token).ConfigureAwait(false); }
            catch { _requests.Enqueue(request); throw; }
            if (run is null) return;
            if (run.FinishedAt is { } finished)
            {
                // 履歴確定後に予定の保存だけが失敗した場合も、同じ取得を繰り返さず修復する。
                try
                {
                    if (_states.TryGetValue(request.SourceId, out var pending) && pending.ActiveRunId == request.RunId)
                        await SetStateAsync(pending with { ActiveRunId = null, LastFetchedAt = finished, Status = run.Status, Error = run.Error,
                            NextDueAt = source?.Automatic == true ? Later(finished.AddMinutes(source.IntervalMinutes), source.Kind == ContentSourceKind.GitHubReleases ? GitHubWaitUntil : null) : null }, token).ConfigureAwait(false);
                }
                catch { _requests.Enqueue(request); throw; }
                return;
            }
            _fetching = true;
            try
            {
                if (source is null || source.Url != run.Source) { run = run with { Status = "情報源削除・変更により中止" }; return; }
                await SetStateAsync(_states[source.Id] with { Status = "取得中" }, token).ConfigureAwait(false);
                if (source.Kind == ContentSourceKind.GitHubReleases && GitHubWaitUntil is { } waiting)
                    throw new GitHubFetchException("GitHub API のレート制限解除まで取得を待機しています。", waiting);
                var items = FeedSelection.Latest(await feed.FetchAsync(source, token).ConfigureAwait(false));
                await _mutationGate.WaitAsync(token).ConfigureAwait(false);
                int added;
                try
                {
                    if (!_configuration.Sources.Any(item => item.Id == source.Id && !item.Deleted && item.Url == source.Url && item.Kind == source.Kind && item.IncludePrereleases == source.IncludePrereleases))
                    { run = run with { Status = "情報源変更により中止" }; return; }
                    added = await articles.RegisterAsync(items, _time.GetUtcNow(), token).ConfigureAwait(false);
                }
                finally { _mutationGate.Release(); }
                run = run with { Status = "取得完了（要約は別処理）", Fetched = items.Count, New = added, Duplicates = items.Count - added };
                Record("fetch.completed", $"取得 {items.Count}件・新規 {added}件・重複 {items.Count - added}件。", run.Id);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (GitHubFetchException exception)
            {
                diagnostics.Error("worker.github", exception);
                if (exception.RetryAt is { } retry)
                {
                    Interlocked.Exchange(ref _githubRetryTicks, Later(retry, GitHubWaitUntil).UtcTicks);
                }
                run = run with { Status = exception.RetryAt is null ? "取得失敗" : "レート制限待機", Error = exception.Message };
                _lastError = run.Error; Record("fetch.github-failed", run.Error, run.Id, AppLogLevel.Warning);
            }
            catch (Exception exception)
            {
                diagnostics.Error("worker.fetch", exception);
                run = run with { Status = "取得失敗", Error = source?.Kind == ContentSourceKind.GitHubReleases ? "GitHub の取得処理に失敗しました。次回予定で再試行します。" : "フィードを取得できません。次回予定で再試行します。" };
                _lastError = run.Error; Record("fetch.failed", run.Error, run.Id, AppLogLevel.Warning);
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var interrupted = token.IsCancellationRequested;
                try
                {
                    await articles.SaveRunAsync(interrupted ? run with { Status = "中断・再開待ち" } : run with { FinishedAt = _time.GetUtcNow() }, cleanup.Token).ConfigureAwait(false);
                    var current = _configuration.Sources.FirstOrDefault(item => item.Id == request.SourceId && !item.Deleted);
                    var state = _states.GetOrAdd(request.SourceId, id => new(id));
                    await SetStateAsync(state with { ActiveRunId = interrupted ? request.RunId : null,
                        LastFetchedAt = interrupted ? state.LastFetchedAt : _time.GetUtcNow(), Status = interrupted ? "中断・再開待ち" : run.Status, Error = run.Error,
                        NextDueAt = current is null ? null : NextDue(current) }, cleanup.Token).ConfigureAwait(false);
                }
                catch { _requests.Enqueue(request); throw; }
                finally { _fetching = false; }
            }
        }
        finally { _fetchGate.Release(); }
    }
    /// <summary>要約ループの1ステップ。最新10件の範囲外へ移った保存済み待機記事も処理する。</summary>
    public async Task TickAiAsync(CancellationToken token)
    {
        if (_configuration.AiPaused || _time.GetUtcNow().UtcTicks < Interlocked.Read(ref _nextAiCheckTicks) || !await _aiGate.WaitAsync(0, token).ConfigureAwait(false)) return;
        try
        {
            if (_configuration.AiPaused) return;
            if (_recoverArticles)
            {
                await articles.RecoverAsync(token).ConfigureAwait(false);
                _recoverArticles = false;
            }
            CollectedArticle? item = null;
            foreach (var source in _configuration.Sources.Where(source => !source.Deleted))
            {
                item = await articles.NextPendingAsync(source.Url, _time.GetUtcNow(), token).ConfigureAwait(false);
                if (item is not null) break;
            }
            if (item is null) { _aiState = "待機中"; return; }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            _aiCancellation = cancellation;
            var operation = cancellation.Token;
            var current = item;
            var began = false;
            var newlyLoaded = false;
            string? loadedModel = null;
            IDisposable? lease = null;
            try
            {
                if (_configuration.AiPaused) return;
                if (current.Summary is null)
                {
                    var model = _configuration.SummaryModel;
                    if (string.IsNullOrWhiteSpace(model)) { _aiState = "要約モデル未設定・待機中"; return; }
                    using var gateDeadline = CancellationTokenSource.CreateLinkedTokenSource(operation);
                    gateDeadline.CancelAfter(TimeSpan.FromSeconds(1));
                    try { lease = await modelGate.EnterAsync(gateDeadline.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!operation.IsCancellationRequested) { _aiState = "チャット等がモデルを使用中・待機中"; return; }
                    if (!(await chat.GetModelsAsync(operation).ConfigureAwait(false)).Any(available => ChatModelSession.SameModel(available, model)))
                    { _aiState = "選択モデルが存在しません・待機中"; return; }
                    var loaded = (await models.GetLoadedModelsAsync(operation).ConfigureAwait(false)).Any(entry => ChatModelSession.SameModel(entry.Name, model));
                    if (!loaded && !_configuration.AllowLoad) { _aiState = "モデル未ロード・自動ロード OFF のため待機中"; Interlocked.Exchange(ref _nextAiCheckTicks, _time.GetUtcNow().AddSeconds(5).UtcTicks); return; }
                    if (!loaded)
                    {
                        operation.ThrowIfCancellationRequested();
                        await models.LoadModelAsync(model, operation).ConfigureAwait(false);
                        newlyLoaded = (await models.GetLoadedModelsAsync(operation).ConfigureAwait(false)).Any(entry => ChatModelSession.SameModel(entry.Name, model));
                        if (!newlyLoaded) throw new WorkerException("モデルのロードを確認できませんでした。");
                        loadedModel = model;
                    }
                    if (_configuration.AiPaused) return;
                    began = await articles.TryBeginAsync(item.Id, operation).ConfigureAwait(false);
                    if (!began) return;
                    _summarizing = true; _aiState = "要約中";
                    var summary = await ArticleSummary.GenerateAsync(chat, model, current.Article, operation).ConfigureAwait(false);
                    current = current with { Summary = summary, Model = model, Error = null };
                    await articles.SaveArticleAsync(current, operation).ConfigureAwait(false);
                }
                else { began = await articles.TryBeginAsync(item.Id, operation).ConfigureAwait(false); if (!began) return; }
                var path = await writer.WriteAsync(current, operation).ConfigureAwait(false);
                await articles.CompleteAsync(current with { MarkdownPath = path, Notified = true, Error = null }, operation).ConfigureAwait(false);
                _aiState = "要約・保存完了"; _lastError = "";
                Record("summary.completed", "記事の要約・Markdown 保存・通知登録が完了しました。", item.Id);
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested)
            { _aiState = "要約を中止しました・再開待ち"; }
            catch (Exception exception)
            {
                diagnostics.Error("worker.summary", exception);
                _lastError = exception is ChatServiceException ? "Ollama の通信またはモデル操作に失敗しました。接続とモデルを確認してください。" :
                    exception is StorageException ? "記事の DB または Markdown 保存に失敗しました。保存先を確認してください。" :
                    "要約に失敗しました。モデルの応答とシステムログを確認してください。";
                _aiState = "再試行可能な失敗"; Record("summary.failed", _lastError, item.Id, AppLogLevel.Warning);
                Interlocked.Exchange(ref _nextAiCheckTicks, _time.GetUtcNow().AddSeconds(30).UtcTicks);
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    if (began) await articles.EndAttemptAsync(item.Id, _aiState == "再試行可能な失敗" ? _lastError : null,
                        _aiState == "再試行可能な失敗" ? _time.GetUtcNow().AddMinutes(1) : null, cleanup.Token).ConfigureAwait(false);
                }
                catch (Exception exception) { _recoverArticles = true; diagnostics.Error("worker.queue.cleanup", exception); }
                finally
                {
                    if (newlyLoaded && loadedModel is not null && _configuration.AllowUnload && lease is not null)
                    {
                        try { await models.UnloadModelAsync(loadedModel, cleanup.Token).ConfigureAwait(false); }
                        catch (Exception exception) { diagnostics.Error("worker.model.release", exception); }
                    }
                    lease?.Dispose(); _aiCancellation = null; _summarizing = false;
                }
            }
        }
        finally { _aiGate.Release(); }
    }
    /// <summary>実行中 AI を中止してから、取得登録とも排他し、選択記事だけを削除する。</summary>
    public async Task<ArticleDeletionResult> DeleteAsync(IReadOnlyList<Guid> ids, CancellationToken token)
    {
        if (ids is null || ids.Count is < 1 or > 100 || ids.Any(id => id == Guid.Empty)) throw new WorkerException("削除対象は1〜100件で指定してください。");
        CancelAi();
        await _aiGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await _mutationGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var deleted = new List<Guid>(); var failed = new List<Guid>(); var error = "";
                foreach (var id in ids.Distinct())
                {
                    token.ThrowIfCancellationRequested();
                    try { await articles.DeleteArticleAsync(id, token).ConfigureAwait(false); deleted.Add(id); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception) { diagnostics.Error("worker.delete", exception); failed.Add(id); error = "一部の記事を削除できませんでした。保存先を確認して再試行してください。"; }
                }
                Record("articles.deleted", $"削除 {deleted.Count}件・失敗 {failed.Count}件。");
                return new(deleted, failed, false, error);
            }
            finally { _mutationGate.Release(); }
        }
        finally { _aiGate.Release(); }
    }
    /// <summary>停止時に実行中の要約を安全に中止する。</summary>
    public void CancelAi()
    {
        try { _aiCancellation?.Cancel(); }
        catch (ObjectDisposedException) { /* ちょうど終了した生成への中止要求は不要。 */ }
    }
    /// <summary>設定を検証して、外部 URL の正規化とスケジュールの上限を適用する。</summary>
    public static WorkerConfiguration Validate(WorkerConfiguration value)
    {
        if (value.Version < 0 || value.SummaryModel?.Length > 512 || value.Sources is null || value.Sources.Count > 50 ||
            value.Sources.Any(source => source is null || source.Id == Guid.Empty || string.IsNullOrWhiteSpace(source.Name) || source.Name.Length > 120 ||
                source.IntervalMinutes is < 5 or > 10080 || string.IsNullOrWhiteSpace(source.Url) || source.Url.Length > 4096) || value.Sources.Select(source => source.Id).Distinct().Count() != value.Sources.Count)
            throw new WorkerException("情報源名は1〜120文字、巡回周期は5〜10080分、情報源は最大50件で指定してください。");
        var sources = value.Sources.Select(source => source with { Name = source.Name.Trim(), Url = ContentSourceIdentity.Normalize(source.Kind, source.Url), IncludePrereleases = source.Kind == ContentSourceKind.GitHubReleases && source.IncludePrereleases }).ToArray();
        if (sources.Any(source => source.Url.Length > 4096)) throw new WorkerException("正規化後の URL は4096文字以内で指定してください。");
        if (sources.Select(source => source.Url).Distinct(StringComparer.Ordinal).Count() != sources.Length) throw new WorkerException("同じ URL の情報源は重複登録できません。削除済みの情報源を再利用してください。");
        return value with { Sources = sources, SummaryModel = string.IsNullOrWhiteSpace(value.SummaryModel) ? null : value.SummaryModel.Trim() };
    }
    /// <summary>削除済み情報源の状態も含め、共有する API の待機を復元する。</summary>
    private DateTimeOffset? GitHubWaitUntil => Interlocked.Read(ref _githubRetryTicks) is var ticks && ticks > _time.GetUtcNow().UtcTicks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
    /// <summary>定期予定を API 待機期限より前にしない。</summary>
    private DateTimeOffset? NextDue(SourceConfiguration source) => source.Automatic && !source.Deleted ? Later(_time.GetUtcNow().AddMinutes(source.IntervalMinutes), source.Kind == ContentSourceKind.GitHubReleases ? GitHubWaitUntil : null) : null;
    /// <summary>通常予定と制限解除の遅い方。</summary>
    private static DateTimeOffset Later(DateTimeOffset at, DateTimeOffset? other) => other > at ? other.Value : at;
    /// <summary>設定編集と取得完了が重なっても共有待機を古い状態で巻き戻さない。</summary>
    private async Task SetStateAsync(SourceState state, CancellationToken token)
    {
        await _stateGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // GitHub の待機は IP 共有の状態。RSS の行にも保存でき、情報源削除後も復元する。
            if (Interlocked.Read(ref _githubRetryTicks) is var ticks && ticks > 0)
                state = state with { GitHubRetryAt = Later(new DateTimeOffset(ticks, TimeSpan.Zero), state.GitHubRetryAt) };
            await settings.SaveStateAsync(state, token).ConfigureAwait(false); _states[state.Id] = state;
        }
        finally { _stateGate.Release(); }
    }
    private void Record(string operation, string message, Guid? id = null, AppLogLevel level = AppLogLevel.Information) => log.Write(AppLogCategory.Application, level, operation, message, id);
}
