using Ateliers.Ai.Assistant.Application.Chat;
using Ateliers.Ai.Assistant.Application.Notifications;

namespace Ateliers.Ai.Assistant.Application.WebMonitoring;

/// <summary>フィードが提供した記事。本文の別途取得は行わない。</summary>
public sealed record FeedArticle(string Key, string Source, string? ExternalId, string? Url, string Title,
    DateTimeOffset? PublishedAt, string Content, string InputScope, GitHubReleaseMetadata? GitHubRelease = null);
/// <summary>再試行に必要な原文と、各段階の確定結果。</summary>
public sealed record CollectedArticle(Guid Id, FeedArticle Article, DateTimeOffset FetchedAt,
    string? Summary = null, string? Model = null, string? MarkdownPath = null, bool Notified = false, string? Error = null,
    bool Processing = false, DateTimeOffset? RetryAfter = null);
/// <summary>UI の履歴表示だけに必要な読取契約。</summary>
public interface IWebMonitoringReader
{
    /// <summary>最新の巡回履歴。</summary>
    Task<IReadOnlyList<CollectionRun>> RunsAsync(CancellationToken token);
    /// <summary>最新の通知。</summary>
    Task<IReadOnlyList<ArticleNotification>> NotificationsAsync(CancellationToken token);
}
/// <summary>収集開始と削除の UI 用契約。Worker クライアントへ差し替えられる。</summary>
public interface IWebCollectionOperations
{
    /// <summary>1件の情報源の取得を要求する。</summary>
    Task CollectAsync(string url, string? model, IProgress<string>? progress, CancellationToken token);
    /// <summary>選択した記事を削除する。</summary>
    Task<ArticleDeletionResult> DeleteAsync(IReadOnlyList<Guid> ids, CancellationToken token);
}
/// <summary>巡回の開始・終了と、今回の処理件数。</summary>
public sealed record CollectionRun(Guid Id, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt = null,
    string Status = "実行中", int Fetched = 0, int New = 0, int Duplicates = 0, int Summarized = 0,
    int Failed = 0, string Error = "", string Source = "", string? Model = null);
/// <summary>共通通知と参照先。SourceUrl は記事 URL、FeedSourceUrl は保存済みの取得元フィード URL。</summary>
public sealed record ArticleNotification(NotificationDto Notification, string? SourceUrl, string MarkdownPath,
    DateTimeOffset? PublishedAt = null, string? SummaryModel = null, string? FeedSourceUrl = null,
    DateTimeOffset? FetchedAt = null, DateTimeOffset? ReadAt = null, bool IsBookmarked = false, IReadOnlyList<ArticleTag>? Tags = null,
    GitHubReleaseMetadata? GitHubRelease = null);
/// <summary>一括削除の結果。失敗・中止した対象は残し、成功した対象だけを削除済みとして扱う。</summary>
public sealed record ArticleDeletionResult(IReadOnlyList<Guid> DeletedIds, IReadOnlyList<Guid> FailedIds, bool Canceled, string Error);
/// <summary>安全なフィード HTTP 取得と RSS / Atom 解析の境界。</summary>
public interface IFeedSource : IContentSource
{
    /// <summary>指定フィードの正規化済み記事を取得する。</summary>
    Task<IReadOnlyList<FeedArticle>> FetchAsync(string url, CancellationToken cancellationToken);
    /// <summary>既存 RSS アダプター・テストを共通取得ポートへ接続する。</summary>
    Task<IReadOnlyList<FeedArticle>> IContentSource.FetchAsync(Ateliers.Ai.Assistant.Application.Worker.SourceConfiguration source, CancellationToken token) =>
        source.Kind == ContentSourceKind.RssAtom ? FetchAsync(source.Url, token) : throw new WebMonitoringException("この取得処理は RSS / Atom 専用です。");
}
/// <summary>収集状態、巡回履歴、通知の永続化境界。</summary>
public interface IWebMonitoringStore : IWebMonitoringReader
{
    /// <summary>巡回履歴を登録または更新する。</summary>
    Task SaveRunAsync(CollectionRun run, CancellationToken token);
    /// <summary>重複を無視して新規記事を登録し、追加件数を返す。</summary>
    Task<int> RegisterAsync(IReadOnlyList<FeedArticle> articles, DateTimeOffset fetchedAt, CancellationToken token);
    /// <summary>当該フィードの未完了記事を取得する。前回失敗した記事も含む。</summary>
    Task<IReadOnlyList<CollectedArticle>> PendingAsync(string source, CancellationToken token);
    /// <summary>要約成功または失敗を記録する。通知とは別の段階として保持する。</summary>
    Task SaveArticleAsync(CollectedArticle article, CancellationToken token);
    /// <summary>ファイル保存の確定と通知1件の作成を同じトランザクションで行う。</summary>
    Task CompleteAsync(CollectedArticle article, CancellationToken token);
    /// <summary>最新の巡回履歴を取得する。中断された実行も表示する。</summary>
    new Task<IReadOnlyList<CollectionRun>> RunsAsync(CancellationToken token);
    /// <summary>最新の永続通知を取得する。</summary>
    new Task<IReadOnlyList<ArticleNotification>> NotificationsAsync(CancellationToken token);
    /// <summary>記事・通知・固定パスの Markdown を削除する。重複判定キーも失われ、次回は新規登録できる。</summary>
    Task DeleteArticleAsync(Guid id, CancellationToken token);
}
/// <summary>記事を安定した内部パスの UTF-8 Markdown に保存する。</summary>
public interface IArticleMarkdownWriter
{
    /// <summary>原子的に保存し、Local 配下の相対パスを返す。</summary>
    Task<string> WriteAsync(CollectedArticle article, CancellationToken token);
}
/// <summary>複数画面のモデル使用と停止操作を同じ排他区間で調整する。</summary>
public interface IModelWorkCoordinator
{
    /// <summary>操作中だけ有効なチャットポートを渡す。通常チャット履歴とは独立する。</summary>
    Task RunExclusiveAsync(Func<IChatService, CancellationToken, Task> work, CancellationToken token);
}
/// <summary>収集処理の利用者向けエラー。外部応答や原文を診断ログへ展開しない。</summary>
public sealed class WebMonitoringException(string message, Exception? inner = null) : Exception(message, inner);
