using Ateliers.Ai.Assistant.Application.Worker;

namespace Ateliers.Ai.Assistant.Application.ScreenAnalysis;

/// <summary>MVP 後の複数監視で共有する安全上限。UI 設定にはしない。</summary>
public static class ScreenMonitorLimits
{
    /// <summary>停止中も含む登録上限。</summary>
    public const int MaximumMonitors = 4;
    /// <summary>監視ごとのコメント件数。</summary>
    public const int MaximumCommentsPerMonitor = 200;
    /// <summary>Worker 全体の本文 UTF-16 概算バイト数。</summary>
    public const int MaximumCommentBytes = 1024 * 1024;
    /// <summary>ロードから解析までの一回全体の期限。</summary>
    public static TimeSpan OperationTimeout => TimeSpan.FromMinutes(10);
}
/// <summary>不変 ID を持つ保存定義。対象・画像・稼働状態は含めない。</summary>
public sealed record ScreenMonitorDefinition(Guid MonitorId, string Name, ScreenMonitorSettings Settings, long Revision = 0);
/// <summary>ID 指定更新の入力。異なる UI の同一監視上書きを検出する。</summary>
public sealed record UpdateScreenMonitorRequest(string Name, ScreenMonitorSettings Settings, long Revision);
/// <summary>追加の入力。定義 ID は Worker が生成する。</summary>
public sealed record CreateScreenMonitorRequest(string Name);
/// <summary>画像を含まない一覧。コメント本文は選択した監視だけ別途取得する。</summary>
public sealed record ScreenMonitorOverview(ScreenMonitorDefinition Definition, string? TargetName, bool Active, bool Picking,
    bool Processing, bool Deleting, string State, string Error, DateTimeOffset? NextCaptureAt,
    DateTimeOffset? LastAnalysisAt, long Captures, long Skipped, string SkipReason);
/// <summary>Worker 世代と全監視の軽量スナップショット。</summary>
public sealed record ScreenMonitorList(Guid InstanceId, IReadOnlyList<ScreenMonitorOverview> Monitors, int MaximumMonitors);
/// <summary>世代・対象を区別する個別コメント表示。</summary>
public sealed record ScreenMonitorDetail(Guid InstanceId, Guid MonitorId, Guid CaptureSessionId, Guid TargetId,
    IReadOnlyList<ScreenMonitorComment> Comments, ScreenMonitorOverview? State = null);
/// <summary>複数監視を明示 ID で操作するポート。画像転送は行わない。</summary>
public interface IMultiScreenMonitorClient
{
    /// <summary>全監視の状態を取得する。</summary>
    Task<ScreenMonitorList> ListMonitorsAsync(CancellationToken token = default);
    /// <summary>指定監視のコメントを取得する。</summary>
    Task<ScreenMonitorDetail> GetMonitorAsync(Guid id, CancellationToken token = default);
    /// <summary>停止中の新規定義を追加する。</summary>
    Task<ScreenMonitorOverview> CreateMonitorAsync(string name, CancellationToken token = default);
    /// <summary>指定監視の定義を競合検出付きで保存する。</summary>
    Task<ScreenMonitorOverview> UpdateMonitorAsync(Guid id, UpdateScreenMonitorRequest request, CancellationToken token = default);
    /// <summary>指定監視の標準対象選択を開始する。</summary>
    Task<ScreenMonitorOverview> PickMonitorAsync(Guid id, CancellationToken token = default);
    /// <summary>指定監視を開始する。</summary>
    Task<ScreenMonitorOverview> StartMonitorAsync(Guid id, CancellationToken token = default);
    /// <summary>指定監視へ中止を通知する。実際の処理終了までは処理中状態を保持する。</summary>
    Task<ScreenMonitorOverview> StopMonitorAsync(Guid id, CancellationToken token = default);
    /// <summary>処理終了・資源解放後に指定定義を削除する。</summary>
    Task<ScreenMonitorList> DeleteMonitorAsync(Guid id, CancellationToken token = default);
    /// <summary>全監視へ中止を通知する。</summary>
    Task<ScreenMonitorList> StopAllMonitorsAsync(CancellationToken token = default);
}

/// <summary>再起動後も保持する入力値。対象・実行状態・画像は保存しない。</summary>
public sealed record ScreenMonitorSettings(string? Model = null, int IntervalSeconds = 60,
    string Prompt = "この画面の現在の状況を日本語で簡潔に説明し、気付いた点をコメントしてください。")
{
    /// <summary>製品で選択可能な周期。技術検証の短周期はこの設定に含めない。</summary>
    public static IReadOnlyList<int> Intervals { get; } = Array.AsReadOnly(new[] { 10, 30, 60, 120, 300 });
    /// <summary>モデル名・周期・プロンプトの上限を検証する。能力は推測しない。</summary>
    public static ScreenMonitorSettings Validate(ScreenMonitorSettings value)
    {
        if (value is null || !Intervals.Contains(value.IntervalSeconds) || value.Model?.Length > 512 || string.IsNullOrWhiteSpace(value.Prompt) || value.Prompt.Length > 8000)
            throw new WorkerException("監視周期は10・30・60・120・300秒、プロンプトは1〜8000文字で指定してください。");
        return value with { Model = string.IsNullOrWhiteSpace(value.Model) ? null : value.Model.Trim() };
    }
}
/// <summary>画像を含まない、Worker メモリ内のコメント。原文は Markdown のまま。</summary>
public sealed record ScreenMonitorComment(Guid Id, DateTimeOffset CapturedAt, string Model, string Content, string Status);
