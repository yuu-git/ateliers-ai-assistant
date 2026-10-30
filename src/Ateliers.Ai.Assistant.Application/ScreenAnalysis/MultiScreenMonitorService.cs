using System.Text;
using Ateliers.Ai.Assistant.Application.Chat;
using Ateliers.Ai.Assistant.Application.Models;
using Ateliers.Ai.Assistant.Application.Storage;
using Ateliers.Ai.Assistant.Application.Worker;
using Ateliers.Ai.Assistant.Core.Chat;

namespace Ateliers.Ai.Assistant.Application.ScreenAnalysis;

/// <summary>Worker 内の全監視を所有する。取得資格は監視ごとに一つ、実処理は全体一つ。</summary>
public sealed class MultiScreenMonitorService : IAsyncDisposable
{
    private sealed class Monitor(ScreenMonitorDefinition definition, IScreenCaptureService capture)
    {
        internal ScreenMonitorDefinition Definition = definition;
        internal readonly IScreenCaptureService Capture = capture;
        internal readonly List<ScreenMonitorComment> Comments = [];
        internal Guid SessionId = Guid.NewGuid(), TargetId = Guid.NewGuid();
        internal CancellationTokenSource? Cancellation, Selection;
        internal Task Picker = Task.CompletedTask;
        internal EventHandler? Closed;
        internal bool Active, Picking, Processing, Deleting, Ready, ExternalCancellation;
        internal DateTimeOffset? Next, LastAnalysis, LastStarted;
        internal long Captures, Skipped;
        internal string State = "停止中", FinalState = "停止中", Error = "", SkipReason = "";
    }
    private readonly Func<IScreenCaptureService> _createCapture;
    private readonly IChatService _chat;
    private readonly IModelControlService _models;
    private readonly IModelUseGate _modelGate;
    private readonly CapturedImageStore _images;
    private readonly WorkerCoordinator _worker;
    private readonly IAppDiagnostics _diagnostics;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Monitor> _monitors = [];
    private readonly HashSet<string> _loadedByWorker = new(StringComparer.Ordinal);
    private Task _work = Task.CompletedTask;
    private bool _closing, _paused;
    private DateTimeOffset _nextRelease;
    /// <summary>状態が変わったことを、内容を含めずトレイへ通知する。</summary>
    public event EventHandler? Changed;
    /// <summary>現在の実処理が終了するまで待つ。待機取消で実行枠を解放しない。</summary>
    public Task WaitForIdleAsync(CancellationToken token = default) { lock (_sync) return _work.WaitAsync(token); }
    /// <summary>全体で画像ストアとモデル排他を一つだけ接続する。</summary>
    public MultiScreenMonitorService(Func<IScreenCaptureService> createCapture, IChatService chat, IModelControlService models,
        IModelUseGate modelGate, CapturedImageStore images, WorkerCoordinator worker, IAppDiagnostics diagnostics, TimeProvider? clock = null)
    {
        _createCapture = createCapture; _chat = chat; _models = models; _modelGate = modelGate;
        _images = images; _worker = worker; _diagnostics = diagnostics; _clock = clock ?? TimeProvider.System;
        _paused = worker.Configuration.AiPaused;
        foreach (var definition in worker.Configuration.ScreenMonitors ?? []) AddRuntime(definition);
        worker.ConfigurationChanged += ConfigurationChanged;
    }
    /// <summary>軽量一覧は監視 ID により一貫した状態を返す。</summary>
    public ScreenMonitorList List()
    { lock (_sync) return new(_worker.InstanceId, _monitors.Values.Select(Overview).ToArray(), ScreenMonitorLimits.MaximumMonitors); }
    /// <summary>コメントは指定監視だけを返す。</summary>
    public ScreenMonitorDetail Detail(Guid id)
    { lock (_sync) { var monitor = Find(id); return new(_worker.InstanceId, id, monitor.SessionId, monitor.TargetId, monitor.Comments.ToArray(), Overview(monitor)); } }
    private Monitor Find(Guid id) => _monitors.GetValueOrDefault(id) ?? throw new WorkerException("指定した監視は削除済み、または存在しません。");
    private static ScreenMonitorOverview Overview(Monitor m) => new(m.Definition, m.Capture.TargetName, m.Active, m.Picking,
        m.Processing, m.Deleting, m.State, m.Error, m.Next, m.LastAnalysis, m.Captures, m.Skipped, m.SkipReason);
    private void AddRuntime(ScreenMonitorDefinition definition)
    {
        var monitor = new Monitor(definition, _createCapture());
        monitor.Closed = (_, _) => { lock (_sync) RequestStop(monitor, "対象消失により停止しました"); Changed?.Invoke(this, EventArgs.Empty); };
        monitor.Capture.TargetClosed += monitor.Closed; _monitors.Add(definition.MonitorId, monitor);
    }
    /// <summary>同じ正規モデル名の全予約・実使用を集計し、自動解放を制限する。</summary>
    public bool ProtectsModel(string model)
    { lock (_sync) return _monitors.Values.Any(m => (m.Active || m.Processing) && m.Definition.Settings.Model is { } name && ChatModelSession.SameModel(model, name)); }
    /// <summary>要約で本 Worker がロードしたモデルも、停止後の管理対象として引き継ぐ。</summary>
    public void TrackLoadedModel(string model) { lock (_sync) if (!_loadedByWorker.Any(name => ChatModelSession.SameModel(model, name))) _loadedByWorker.Add(model); }
    /// <summary>別の用途が解放したことを共有する。</summary>
    public void ModelReleased(string model) { lock (_sync) foreach (var entry in _loadedByWorker.Where(name => ChatModelSession.SameModel(model, name)).ToArray()) _loadedByWorker.Remove(entry); }
    private void ConfigurationChanged(object? sender, EventArgs args)
    {
        lock (_sync)
        {
            var paused = _worker.Configuration.AiPaused;
            if (paused == _paused) return;
            _paused = paused;
            foreach (var m in _monitors.Values.Where(m => m.Active))
            {
                if (paused) { m.ExternalCancellation = true; m.Cancellation?.Cancel(); m.Ready = false; m.Next = null; m.State = "AI 一時停止中（取得も停止）"; }
                else { m.Next = _clock.GetUtcNow(); m.State = "再開待機中"; }
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    /// <summary>最新設定と ID の重複を検証し、保存成功後だけ実行時状態を追加する。</summary>
    public async Task<ScreenMonitorOverview> CreateAsync(string name, CancellationToken token)
    {
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (string.IsNullOrWhiteSpace(name)) throw new WorkerException("監視名を入力してください。");
            var definition = new ScreenMonitorDefinition(Guid.NewGuid(), name.Trim(), new());
            lock (_sync) if (_closing) throw new WorkerException("Worker を終了しています。");
            await _worker.UpdateMonitorsAsync(items => items.Append(definition).ToArray(), token).ConfigureAwait(false);
            lock (_sync) { AddRuntime(definition); return Overview(Find(definition.MonitorId)); }
        }
        finally { _commands.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }
    /// <summary>当該監視の処理が完全に終わっている時だけ定義を更新する。</summary>
    public async Task<ScreenMonitorOverview> UpdateAsync(Guid id, UpdateScreenMonitorRequest request, CancellationToken token)
    {
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Monitor m; lock (_sync) { m = Find(id); EnsureEditable(m); }
            if (string.IsNullOrWhiteSpace(request.Name)) throw new WorkerException("監視名を入力してください。");
            if (request.Revision != m.Definition.Revision) throw new WorkerException("別の画面で監視設定が変更されました。再取得してください。");
            var next = m.Definition with { Name = request.Name.Trim(), Settings = ScreenMonitorSettings.Validate(request.Settings), Revision = checked(request.Revision + 1) };
            await _worker.UpdateMonitorsAsync(items => items.Select(item => item.MonitorId == id ? next : item).ToArray(), token).ConfigureAwait(false);
            lock (_sync) { m.Definition = next; return Overview(m); }
        }
        finally { _commands.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }
    private void EnsureEditable(Monitor m)
    { if (_closing || m.Active || m.Processing || m.Picking || m.Deleting) throw new WorkerException("当該監視の停止・処理終了・対象選択終了を待ってから操作してください。"); }
    /// <summary>Picker の実操作完了まで所有監視を保持する。他の監視の周期は止めない。</summary>
    public async Task<ScreenMonitorOverview> PickAsync(Guid id, CancellationToken token)
    {
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                var m = Find(id); EnsureEditable(m);
                if (_monitors.Values.Any(item => item.Picking)) throw new WorkerException("別の監視で対象選択中です。選択画面を閉じてください。");
                m.Picking = true; m.Selection = new(); m.Error = ""; m.State = "対象選択中";
                m.Picker = PickCoreAsync(m, m.Selection.Token); return Overview(m);
            }
        }
        finally { _commands.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }
    private async Task PickCoreAsync(Monitor m, CancellationToken token)
    {
        await Task.Yield();
        try
        {
            var selected = await m.Capture.PickAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (selected) { m.TargetId = Guid.NewGuid(); m.SessionId = Guid.NewGuid(); }
                m.State = selected ? "対象選択済み・停止中" : "選択キャンセル・停止中";
            }
        }
        catch (OperationCanceledException) { lock (_sync) m.State = "選択結果を破棄しました・停止中"; }
        catch (Exception exception) { _diagnostics.Error("screen.pick", exception); lock (_sync) { m.State = "対象選択失敗"; m.Error = "対象選択を完了できません。選択画面を閉じて再試行してください。"; } }
        finally { lock (_sync) { m.Picking = false; m.Selection?.Dispose(); m.Selection = null; } Changed?.Invoke(this, EventArgs.Empty); }
    }
    /// <summary>新しい実行世代を開始する。定義 ID と過去コメントは維持する。</summary>
    public async Task<ScreenMonitorOverview> StartAsync(Guid id, CancellationToken token)
    {
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                var m = Find(id); EnsureEditable(m);
                if (_worker.Configuration.AiPaused) throw new WorkerException("AI を再開してから監視を開始してください。");
                if (m.Capture.TargetName is null || string.IsNullOrWhiteSpace(m.Definition.Settings.Model)) throw new WorkerException("対象とモデルを選択してください。");
                m.Active = true; m.Error = ""; m.State = "監視中"; m.SessionId = Guid.NewGuid(); m.Next = _clock.GetUtcNow();
                m.Ready = false; m.Captures = m.Skipped = 0; m.SkipReason = ""; return Overview(m);
            }
        }
        finally { _commands.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }
    private static void RequestStop(Monitor m, string state)
    { m.Active = m.Ready = false; m.Next = null; m.FinalState = state; m.ExternalCancellation = true; m.Cancellation?.Cancel(); m.Selection?.Cancel(); m.State = m.Processing ? "停止処理中" : state; }
    /// <summary>処理終了を偽装せず、中止要求を即時返す。</summary>
    public ScreenMonitorOverview Stop(Guid id)
    { lock (_sync) { var m = Find(id); RequestStop(m, "停止中"); Changed?.Invoke(this, EventArgs.Empty); return Overview(m); } }
    /// <summary>全体の取消は一括通知し、処理中の枠は終了まで保持する。</summary>
    public ScreenMonitorList StopAll(string reason = "全監視を停止しました")
    { lock (_sync) { foreach (var m in _monitors.Values) RequestStop(m, reason); Changed?.Invoke(this, EventArgs.Empty); return List(); } }
    /// <summary>処理中は削除予約だけ行い、取得リース解放後の tick で削除を確定する。</summary>
    public async Task<ScreenMonitorList> DeleteAsync(Guid id, CancellationToken token)
    {
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                var m = Find(id); if (m.Picking) throw new WorkerException("対象選択画面を閉じてから削除してください。");
                RequestStop(m, "削除待機中"); m.Deleting = true;
            }
            await FinalizeDeletesAsync(token).ConfigureAwait(false); return List();
        }
        finally { _commands.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }
    private async Task FinalizeDeletesAsync(CancellationToken token)
    {
        Monitor[] ready; lock (_sync) ready = _monitors.Values.Where(m => m.Deleting && !m.Processing && !m.Picking).ToArray();
        foreach (var m in ready)
        {
            try
            {
                await _worker.UpdateMonitorsAsync(items => items.Where(item => item.MonitorId != m.Definition.MonitorId).ToArray(), token).ConfigureAwait(false);
                lock (_sync) { m.Capture.TargetClosed -= m.Closed; m.Capture.Dispose(); m.Comments.Clear(); _monitors.Remove(m.Definition.MonitorId); }
            }
            catch (Exception exception) when (!token.IsCancellationRequested)
            { _diagnostics.Error("screen.delete", exception); lock (_sync) { m.Deleting = false; m.State = "停止中"; m.Error = "定義の削除を保存できませんでした。再試行してください。"; } }
        }
    }
    /// <summary>既存 Worker ループから呼ぶ。資格は一つへ集約し、実行できる監視がある限り未来の周期を待たない。</summary>
    public async Task TickAsync(CancellationToken token)
    {
        lock (_sync)
        {
            if (_closing) return;
            var now = _clock.GetUtcNow();
            foreach (var m in _monitors.Values.Where(m => m.Processing && m.LastStarted is { } started && now - started >= ScreenMonitorLimits.OperationTimeout)) m.Cancellation?.Cancel();
            foreach (var m in _monitors.Values.Where(m => m.Active && !m.Deleting))
            {
                if (_worker.Configuration.AiPaused) continue;
                if (m.Next > now) continue;
                m.Next = now.AddSeconds(m.Definition.Settings.IntervalSeconds);
                m.Ready = true;
                if (!_work.IsCompleted) { m.Skipped++; m.SkipReason = "他の監視の処理中・周期を集約"; }
            }
            if (!_work.IsCompleted) return;
        }
        if (!await _commands.WaitAsync(0, token).ConfigureAwait(false)) return;
        try
        {
            await FinalizeDeletesAsync(token).ConfigureAwait(false);
            await ReleaseUnusedModelsAsync(token).ConfigureAwait(false);
            lock (_sync)
            {
                if (_closing || _worker.Configuration.AiPaused || !_work.IsCompleted) return;
                var m = _monitors.Values.Where(m => m.Active && m.Ready && !m.Picking && !m.Deleting)
                    .OrderBy(m => m.LastStarted ?? DateTimeOffset.MinValue).ThenByDescending(m => m.Skipped).ThenBy(m => m.Definition.MonitorId).FirstOrDefault();
                if (m is null) return;
                m.Ready = false; m.LastStarted = _clock.GetUtcNow(); m.Processing = true; m.ExternalCancellation = false;
                m.Cancellation?.Dispose(); m.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                m.Cancellation.CancelAfter(ScreenMonitorLimits.OperationTimeout);
                _work = CycleAsync(m, m.Cancellation.Token);
            }
        }
        finally { _commands.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }
    private async Task CycleAsync(Monitor m, CancellationToken token)
    {
        await Task.Yield();
        Guid? comment = null; CapturedImageInfo? info = null;
        try
        {
            using var gateDeadline = CancellationTokenSource.CreateLinkedTokenSource(token); gateDeadline.CancelAfter(TimeSpan.FromSeconds(1));
            IDisposable lease;
            try { lease = await _modelGate.EnterAsync(gateDeadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { lock (_sync) { m.Skipped++; m.SkipReason = "チャット・要約等のモデル使用中"; m.State = "モデル排他待ち"; } return; }
            using (lease)
            {
                token.ThrowIfCancellationRequested(); if (_worker.Configuration.AiPaused) return;
                var model = m.Definition.Settings.Model!;
                var loaded = (await _models.GetLoadedModelsAsync(token).ConfigureAwait(false)).Any(item => ChatModelSession.SameModel(item.Name, model));
                if (!loaded && !_worker.Configuration.AllowLoad) { lock (_sync) m.State = "モデル未ロード・自動ロード OFF のため待機中"; return; }
                TrackLoadedModel(model);
                if (!loaded) await _models.LoadModelAsync(model, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested(); if (_worker.Configuration.AiPaused) return;
                lock (_sync) m.State = "キャプチャ中";
                var image = await m.Capture.CaptureAsync(token).ConfigureAwait(false); var at = _clock.GetUtcNow();
                info = _images.Register(m.SessionId, m.TargetId, at, image);
                using var frame = _images.Acquire(m.SessionId, m.TargetId, [info.Id]);
                token.ThrowIfCancellationRequested(); if (_worker.Configuration.AiPaused) return;
                var entry = new ScreenMonitorComment(Guid.NewGuid(), at, model, "", "受信中…"); comment = entry.Id;
                lock (_sync) { m.Captures++; m.State = "Vision 解析中"; m.Comments.Add(entry); TrimComments(); }
                var text = new StringBuilder();
                await foreach (var chunk in _chat.StreamReplyAsync(model, [new(ChatRole.User, m.Definition.Settings.Prompt, frame.Images[0].Image)], token).ConfigureAwait(false))
                {
                    token.ThrowIfCancellationRequested(); if (text.Length + chunk.Length > 65536) throw new ChatServiceException("コメントが65536文字を超えました。");
                    text.Append(chunk); UpdateComment(m, entry.Id, text.ToString(), "受信中…");
                }
                token.ThrowIfCancellationRequested(); UpdateComment(m, entry.Id, text.ToString(), "完了");
                lock (_sync) { m.LastAnalysis = _clock.GetUtcNow(); m.State = "監視中"; }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            lock (_sync)
            {
                if (comment is { } id) FinishComment(m, id, "中止");
                if (m.Active && !_worker.Configuration.AiPaused && !m.ExternalCancellation) { RequestStop(m, "時間切れ・停止中"); m.Error = "1回の処理が10分の上限を超えました。"; }
            }
        }
        catch (Exception exception)
        {
            _diagnostics.Error("screen.monitor", exception);
            lock (_sync) { if (comment is { } id) FinishComment(m, id, "失敗"); RequestStop(m, "エラー・停止中"); m.Error = exception is ChatServiceException ? exception.Message : "当該監視の取得または解析に失敗しました。対象とモデルを確認してください。"; }
        }
        finally
        {
            if (info is not null) _images.Remove(info.SessionId, info.TargetId, info.Id);
            lock (_sync)
            {
                m.Cancellation?.Dispose(); m.Cancellation = null;
                m.Processing = false;
                if (m.Active && _worker.Configuration.AiPaused) m.State = "AI 一時停止中（取得も停止）";
                else if (!m.Active && m.State == "停止処理中") m.State = m.Deleting ? "削除待機中" : m.FinalState;
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
    private void UpdateComment(Monitor m, Guid id, string content, string state)
    { lock (_sync) { var index = m.Comments.FindIndex(item => item.Id == id); if (index >= 0) m.Comments[index] = m.Comments[index] with { Content = content, Status = state }; TrimComments(); } }
    private static void FinishComment(Monitor m, Guid id, string state)
    { var index = m.Comments.FindIndex(item => item.Id == id); if (index >= 0) m.Comments[index] = m.Comments[index] with { Status = state }; }
    private void TrimComments()
    {
        foreach (var m in _monitors.Values) while (m.Comments.Count > ScreenMonitorLimits.MaximumCommentsPerMonitor) m.Comments.RemoveAt(0);
        while (_monitors.Values.Sum(m => m.Comments.Sum(item => (long)item.Content.Length * 2)) > ScreenMonitorLimits.MaximumCommentBytes)
        {
            var oldest = _monitors.Values.SelectMany(m => m.Comments.Where(c => c.Status != "受信中…").Select(c => (Monitor: m, Comment: c)))
                .OrderBy(item => item.Comment.CapturedAt).FirstOrDefault();
            if (oldest.Monitor is null) break; oldest.Monitor.Comments.Remove(oldest.Comment);
        }
    }
    private async Task ReleaseUnusedModelsAsync(CancellationToken token)
    {
        if (_clock.GetUtcNow() < _nextRelease && !_closing) return;
        string[] unused; lock (_sync) unused = _loadedByWorker.Where(model => !ProtectsModel(model)).ToArray();
        if (!_worker.Configuration.AllowUnload) { lock (_sync) foreach (var model in unused) _loadedByWorker.Remove(model); return; }
        if (unused.Length == 0) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            using var gate = await _modelGate.EnterAsync(deadline.Token).ConfigureAwait(false);
            foreach (var model in unused)
            {
                if (ProtectsModel(model)) continue;
                if ((await _models.GetLoadedModelsAsync(deadline.Token).ConfigureAwait(false)).Any(item => ChatModelSession.SameModel(item.Name, model)))
                    await _models.UnloadModelAsync(model, deadline.Token).ConfigureAwait(false);
                lock (_sync) _loadedByWorker.Remove(model);
            }
        }
        catch (Exception exception) when (!token.IsCancellationRequested)
        { _nextRelease = _clock.GetUtcNow().AddSeconds(30); _diagnostics.Error("screen.model.cleanup", exception); lock (_sync) foreach (var m in _monitors.Values.Where(m => !m.Active)) m.Error = "監視は停止しましたが、モデル解放は保留中です。モデル管理で状態を確認してください。"; }
    }
    /// <summary>終了時は中止を一括通知する。中止を無視する処理の完了前に別処理を開始しない。</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_sync) { _closing = true; StopAll("Worker 終了中"); }
        _worker.ConfigurationChanged -= ConfigurationChanged;
        await _work.ConfigureAwait(false);
        await ReleaseUnusedModelsAsync(default).ConfigureAwait(false);
        lock (_sync) foreach (var m in _monitors.Values) { m.Capture.TargetClosed -= m.Closed; if (!m.Picking) m.Capture.Dispose(); m.Comments.Clear(); }
    }
#if DEBUG
    /// <summary>既知の対象だけを使う実機検証の取得ポート。</summary>
    public IScreenCaptureService CaptureForVerification(Guid id) { lock (_sync) return Find(id).Capture; }
#endif
}
