using Ateliers.Ai.Assistant.Application.WebMonitoring;

namespace Ateliers.Ai.Assistant.Application.Worker;

/// <summary>利用者が設定する情報源。削除指定は巡回だけを停止し、記事の URL 別重複記録は保持する。</summary>
public sealed record SourceConfiguration(Guid Id, string Name, string Url, int IntervalMinutes = 60, bool Automatic = false, bool Deleted = false,
    ContentSourceKind Kind = ContentSourceKind.RssAtom, bool IncludePrereleases = false);
/// <summary>Worker だけが保存する小容量設定。Version で複数 UI の上書き競合を検出する。</summary>
public sealed record WorkerConfiguration(long Version, string? SummaryModel, bool AllowLoad, bool AllowUnload,
    bool AiPaused, IReadOnlyList<SourceConfiguration> Sources)
{
    /// <summary>モデルの自動操作を許可しない初期設定。</summary>
    public static WorkerConfiguration Default => new(0, null, false, false, false, []);
}
/// <summary>情報源の次回予定と、取得処理の永続的な進捗。</summary>
public sealed record SourceState(Guid Id, DateTimeOffset? LastFetchedAt = null, DateTimeOffset? NextDueAt = null,
    string Status = "未実行", string Error = "", Guid? ActiveRunId = null, DateTimeOffset? GitHubRetryAt = null);
/// <summary>UI が表示する Worker の全体状態。秘密トークンは含めない。</summary>
public sealed record WorkerStatus(int Protocol, string Version, Guid InstanceId, DateTimeOffset StartedAt,
    WorkerConfiguration Configuration, IReadOnlyList<SourceState> Sources, int PendingArticles,
    string AiState, string LastError, bool Fetching, bool Summarizing);
/// <summary>冪等な手動取得要求。</summary>
public sealed record FetchRequest(Guid SourceId, Guid RequestId);
/// <summary>既存実行を含む取得要求の受け付け結果。</summary>
public sealed record FetchAccepted(Guid RunId, bool AlreadyRunning);
/// <summary>記事 ID のみで指定する削除要求。</summary>
public sealed record DeleteArticlesRequest(IReadOnlyList<Guid> Ids);
/// <summary>UI と Worker の API 契約。DB や HTTP の実装を公開しない。</summary>
public interface IWorkerClient
{
    /// <summary>状態と設定を読み込む。未起動・認証・版の不一致は別の利用者向けエラーにする。</summary>
    Task<WorkerStatus> GetStatusAsync(CancellationToken token = default);
    /// <summary>競合を検出して設定を保存する。周期変更を即時に次回予定へ反映する。</summary>
    Task<WorkerConfiguration> SaveConfigurationAsync(WorkerConfiguration configuration, CancellationToken token = default);
    /// <summary>自動取得の有効無効によらず1回の取得を要求する。</summary>
    Task<FetchAccepted> FetchAsync(FetchRequest request, CancellationToken token = default);
    /// <summary>Worker で永続履歴を読み込む。</summary>
    Task<IReadOnlyList<CollectionRun>> GetRunsAsync(CancellationToken token = default);
    /// <summary>Worker で通知を読み込む。</summary>
    Task<IReadOnlyList<ArticleNotification>> GetNotificationsAsync(CancellationToken token = default);
    /// <summary>収集・要約と調整して記事を削除する。</summary>
    Task<ArticleDeletionResult> DeleteArticlesAsync(IReadOnlyList<Guid> ids, CancellationToken token = default);
    /// <summary>利用者の明示操作で Worker を停止する。ウィンドウ終了とは独立する。</summary>
    Task<FetchAccepted> StopAsync(CancellationToken token = default);
}
/// <summary>Worker 設定と取得予定の保存境界。</summary>
public interface IWorkerStateStore
{
    /// <summary>初回のみ既存 Web 設定を取り込む。既存設定が不正なら上書きしない。</summary>
    Task<WorkerConfiguration> LoadConfigurationAsync(CancellationToken token);
    /// <summary>一致する Version の設定を保存し、新しい Version を返す。</summary>
    Task<WorkerConfiguration> SaveConfigurationAsync(WorkerConfiguration configuration, CancellationToken token);
    /// <summary>情報源ごとの運転状態を取得する。</summary>
    Task<IReadOnlyList<SourceState>> LoadStatesAsync(CancellationToken token);
    /// <summary>起動復旧に備えて実行状態と予定を確定する。</summary>
    Task SaveStateAsync(SourceState state, CancellationToken token);
}
/// <summary>永続キューの開始・終了・強制終了復旧。取得と要約の間を DB で接続する。</summary>
public interface IBackgroundArticleStore : IWebMonitoringStore
{
    /// <summary>本文を展開せず、稼働中情報源の未完了件数を集計する。</summary>
    Task<int> PendingCountAsync(IReadOnlyList<string> sources, CancellationToken token);
    /// <summary>再試行可能な待機記事を1件だけ読む。</summary>
    Task<CollectedArticle?> NextPendingAsync(string source, DateTimeOffset now, CancellationToken token);
    /// <summary>削除・完了済みの記事は開始せず、処理中を確定する。</summary>
    Task<bool> TryBeginAsync(Guid id, CancellationToken token);
    /// <summary>次の試行予定とエラーを保存し、処理中フラグを解除する。</summary>
    Task EndAttemptAsync(Guid id, string? error, DateTimeOffset? retryAfter, CancellationToken token);
    /// <summary>前回中断した処理を要約待ちに戻す。</summary>
    Task RecoverAsync(CancellationToken token);
    /// <summary>手動要求の再送を同じ実行に結び付ける。</summary>
    Task<CollectionRun?> FindRunAsync(Guid id, CancellationToken token);
}
/// <summary>プロセス間のモデル使用権。チャットと Worker の停止・生成を同時実行させない。</summary>
public interface IModelUseGate
{
    /// <summary>期限内に解放すべき排他権を取得する。</summary>
    Task<IDisposable> EnterAsync(CancellationToken token);
}
/// <summary>Worker API の失敗。秘密情報を含まないメッセージだけを UI へ渡す。</summary>
public sealed class WorkerException(string message, Exception? inner = null, bool connectionFailure = false) : Exception(message, inner)
{
    /// <summary>入力エラーと区別し、接続状態を未接続へ変えるべき障害か。</summary>
    public bool ConnectionFailure { get; } = connectionFailure;
}
