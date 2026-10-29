namespace Ateliers.Ai.Assistant.Application.Worker;

/// <summary>サインイン起動の登録状態。Windows 側で無効化される場合は説明を返す。</summary>
public sealed record WorkerStartupState(bool Registered, string Description);
/// <summary>現在ユーザーの Worker 起動・登録を担う Desktop 側ポート。</summary>
public interface IWorkerLauncher
{
    /// <summary>UI の終了に連動しない独立プロセスを起動する。</summary>
    Task StartAsync();
    /// <summary>自動起動の登録状況を取得する。</summary>
    Task<WorkerStartupState> GetStartupAsync();
    /// <summary>利用者の明示操作で自動起動を登録・解除する。</summary>
    Task<WorkerStartupState> SetStartupAsync(bool enabled);
}
