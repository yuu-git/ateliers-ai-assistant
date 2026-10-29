namespace Ateliers.Ai.Assistant.Application.Worker;

/// <summary>実行中の Worker に AI 一時停止だけを保存する要求。設定の未保存入力を混ぜない。</summary>
public sealed record AiPauseRequest(bool Paused);
/// <summary>設定画面とグローバル操作が共有する、保存済み AI 状態の変更。</summary>
public interface IWorkerAiControl
{
    /// <summary>他の設定を維持して一時停止を保存し、確定した設定を返す。</summary>
    Task<WorkerConfiguration> SetAiPausedAsync(bool paused, CancellationToken token = default);
}
/// <summary>API 接続失敗時だけ確認する、プロセスの単一起動ハンドルの状態。</summary>
public enum WorkerPresence
{
    /// <summary>権限等により判定できない。</summary>
    Unknown,
    /// <summary>Worker のハンドルが存在する。</summary>
    Running,
    /// <summary>ハンドルが保持されていない。</summary>
    Stopped
}
/// <summary>API への接続不能とプロセス停止を区別する補助ポート。</summary>
public interface IWorkerPresenceProbe
{
    /// <summary>接続情報を変更せず、保存環境に対応する実プロセスの存在を調べる。</summary>
    Task<WorkerPresence> GetPresenceAsync(CancellationToken token = default);
}
