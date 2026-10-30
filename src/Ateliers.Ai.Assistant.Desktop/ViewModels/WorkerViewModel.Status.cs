using System;
using System.Linq;
using System.Threading.Tasks;
using Ateliers.Ai.Assistant.Application.Worker;

namespace Ateliers.Ai.Assistant.Desktop.ViewModels;

/// <summary>設定画面とグローバル領域が共有する、実応答に基づく稼働状態・操作。</summary>
public sealed partial class WorkerViewModel
{
    private enum RuntimeState { Unknown, Starting, Running, Stopping, StopFailed, Stopped, Unavailable }
    private RuntimeState _runtimeState;
    private readonly TimeProvider _runtimeClock;
    private bool _stopRequested, _actualAiPaused, _actualSummarizing;
    private bool _knownPresenceRunning;
    private DateTimeOffset _operationDeadline;
    /// <summary>コマンド実行中とプロセスの実状態を区別した表示。</summary>
    public string GlobalWorkerState => _runtimeState switch
    {
        RuntimeState.Starting => "Worker: 起動中（疎通待ち）",
        RuntimeState.Running => "Worker: 稼働中",
        RuntimeState.Stopping => "Worker: 停止処理中（終了確認待ち）",
        RuntimeState.StopFailed => "Worker: 停止未完了（稼働中）",
        RuntimeState.Stopped => "Worker: 停止中",
        RuntimeState.Unavailable => "Worker: 接続不能（稼働状態は未確認）",
        _ => "Worker: 状態不明"
    };
    /// <summary>設定の編集中チェックではなく、最後に確認した実行設定から表示する。</summary>
    public string GlobalAiState => !Connected || _runtimeState is not (RuntimeState.Running or RuntimeState.StopFailed) ? "AI: 状態不明" :
        _actualAiPaused ? "AI: 一時停止中（情報取得は継続）" : _actualSummarizing ? "AI: 処理中" : "AI: 有効 / " + AiText;
    /// <summary>最後に確認した保存済み一時停止状態。</summary>
    public bool ActualAiPaused => _actualAiPaused;
    /// <summary>狭いナビゲーションでも表示する実状態の短い表記。</summary>
    public string CompactWorkerState => _runtimeState switch { RuntimeState.Running => "稼働", RuntimeState.Starting => "起動", RuntimeState.Stopping => "終了", RuntimeState.Stopped => "停止", RuntimeState.StopFailed => "未了", RuntimeState.Unavailable => "不通", _ => "不明" };
    /// <summary>狭幅の AI 状態。保存前のチェックボックスからは算出しない。</summary>
    public string CompactAiState => !Connected || _runtimeState is not (RuntimeState.Running or RuntimeState.StopFailed) ? "AI ?" : _actualAiPaused ? "AI 休" : _actualSummarizing ? "AI 中" : "AI 有";
    /// <summary>実設定に対する次の操作名。</summary>
    public string AiPauseActionText => _actualAiPaused ? "AI を再開（情報取得継続）" : "AI を一時停止（情報取得継続）";
    /// <summary>設定編集を保存せずに、一時停止だけを即時反映する共有コマンド。</summary>
    public AsyncCommand ToggleAiPauseCommand { get; }
    private bool CanStartWorker => !_disposed && !_busy && !Connected && !_knownPresenceRunning && _runtimeState is not (RuntimeState.Starting or RuntimeState.Stopping);
    private bool CanStopWorker => !_disposed && !_busy && Connected && _runtimeState is RuntimeState.Running or RuntimeState.StopFailed;
    private bool CanToggleActualAi => CanStopWorker && _client is IWorkerAiControl;
    private void AcceptRuntimeStatus(WorkerStatus status)
    {
        _runtimeState = _stopRequested ? _runtimeClock.GetUtcNow() < _operationDeadline ? RuntimeState.Stopping : RuntimeState.StopFailed : RuntimeState.Running;
        _knownPresenceRunning = true;
        _actualAiPaused = status.Configuration.AiPaused; _actualSummarizing = status.Summarizing;
        NotifyRuntimeState();
    }
    private async Task StartWorkerAsync()
    {
        _runtimeState = RuntimeState.Starting; _operationDeadline = _runtimeClock.GetUtcNow().AddSeconds(15); NotifyRuntimeState();
        try { await _launcher.StartAsync(); }
        catch { _runtimeState = RuntimeState.Unavailable; NotifyRuntimeState(); throw; }
        await Task.Delay(700, _lifetime.Token);
        await RefreshCoreAsync(false);
    }
    private async Task StopWorkerAsync()
    {
        _stopRequested = true; _runtimeState = RuntimeState.Stopping; _operationDeadline = _runtimeClock.GetUtcNow().AddSeconds(20); NotifyRuntimeState();
        try { await _client.StopAsync(_lifetime.Token); }
        catch (Exception exception) when (exception is not WorkerException { ConnectionFailure: true })
        { _stopRequested = false; _runtimeState = Connected ? RuntimeState.Running : RuntimeState.Unavailable; NotifyRuntimeState(); throw; }
        // 受付応答は終了完了ではない。既存ポーリングと単一起動ハンドルで確認する。
        await RefreshCoreAsync(false);
    }
    private async Task ToggleAiPauseAsync()
    {
        if (_client is not IWorkerAiControl control) return;
        var result = await control.SetAiPausedAsync(!_actualAiPaused, _lifetime.Token);
        var sameOtherSettings = result.SummaryModel == _configuration.SummaryModel && result.AllowLoad == _configuration.AllowLoad &&
            result.AllowUnload == _configuration.AllowUnload && result.Sources.SequenceEqual(_configuration.Sources);
        _configuration = sameOtherSettings ? result : _configuration with { AiPaused = result.AiPaused };
        _actualAiPaused = result.AiPaused;
        _applyingAi = true; try { AiPaused = result.AiPaused; } finally { _applyingAi = false; }
        EditStateChanged(); NotifyRuntimeState(); await RefreshCoreAsync(false);
    }
    private async Task ReconcilePresenceAsync(Exception exception)
    {
        if (_disposed || exception is not WorkerException { ConnectionFailure: true }) return;
        var presence = WorkerPresence.Unknown;
        if (_client is IWorkerPresenceProbe probe)
        { try { presence = await probe.GetPresenceAsync(_lifetime.Token); } catch (Exception failure) { _diagnostics.Error("worker.presence", failure); } }
        _knownPresenceRunning = presence == WorkerPresence.Running;
        if (presence == WorkerPresence.Stopped)
        {
            _stopRequested = false;
            if (_runtimeState == RuntimeState.Starting && _runtimeClock.GetUtcNow() < _operationDeadline) { NotifyRuntimeState(); return; }
            _runtimeState = RuntimeState.Stopped; ConnectionText = "Worker は停止中です。開始操作で巡回を再開できます。";
        }
        else if (_stopRequested && _runtimeClock.GetUtcNow() < _operationDeadline) _runtimeState = RuntimeState.Stopping;
        else if (_runtimeState == RuntimeState.Starting && _runtimeClock.GetUtcNow() < _operationDeadline) { }
        else _runtimeState = RuntimeState.Unavailable;
        NotifyRuntimeState();
    }
    private void NotifyRuntimeState()
    {
        foreach (var property in new[] { nameof(GlobalWorkerState), nameof(GlobalAiState), nameof(ActualAiPaused), nameof(AiPauseActionText), nameof(CompactWorkerState), nameof(CompactAiState) }) OnPropertyChanged(property);
        ToggleAiPauseCommand?.NotifyCanExecuteChanged(); StartCommand?.NotifyCanExecuteChanged(); StopCommand?.NotifyCanExecuteChanged();
    }
}
