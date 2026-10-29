namespace Ateliers.Ai.Assistant.Application.WebMonitoring;

/// <summary>最大100件の通知取得条件。既読・ブックマークとタグは独立する。</summary>
public enum NotificationViewFilter
{
    /// <summary>すべての通知。</summary>
    All,
    /// <summary>未読通知だけ。</summary>
    Unread,
    /// <summary>ブックマーク付き通知だけ。</summary>
    Bookmarked
}
/// <summary>取得元フィードと表示条件。記事 URL は指定しない。</summary>
public sealed record NotificationQuery(string? FeedSourceUrl = null, NotificationViewFilter Filter = NotificationViewFilter.All, string? TagName = null);
/// <summary>記事と独立して再利用する手動タグの定義。</summary>
public sealed record ArticleTag(Guid Id, string Name);
/// <summary>全表示対象通知を DB 集計した未読件数。最新100件の制限を適用しない。</summary>
public sealed record SourceUnreadCount(string FeedSourceUrl, int Count);
/// <summary>条件に合う最大100件と、全体・情報源別の未読数、タグ候補。</summary>
public sealed record NotificationPage(IReadOnlyList<ArticleNotification> Items, int TotalUnread,
    IReadOnlyList<SourceUnreadCount> UnreadBySource, int MatchingCount, IReadOnlyList<ArticleTag> TagCatalog);
/// <summary>既読／未読の明示変更。詳細を自動選択しただけでは送信しない。</summary>
public sealed record SetArticleReadRequest(Guid ArticleId, bool IsRead);
/// <summary>他のメタデータを変えずブックマークを設定する。</summary>
public sealed record SetArticleBookmarkRequest(Guid ArticleId, bool IsBookmarked);
/// <summary>記事へ付与するタグ名。既存名なら定義を再利用する。</summary>
public sealed record AddArticleTagRequest(Guid ArticleId, string Name);
/// <summary>記事とタグの関連だけを解除する。</summary>
public sealed record RemoveArticleTagRequest(Guid ArticleId, Guid TagId);
/// <summary>DB が確定した記事メタデータ。ローカルの予測値で成功扱いにしない。</summary>
public sealed record ArticleMetadata(Guid ArticleId, DateTimeOffset? ReadAt, bool IsBookmarked, IReadOnlyList<ArticleTag> Tags);
/// <summary>Worker が所有する通知管理。HTTP クライアントと DB アダプターで共通の境界を使う。</summary>
public interface INotificationManagement
{
    /// <summary>本文の全件読込を避け、DB 条件と件数集計を適用する。</summary>
    Task<NotificationPage> QueryAsync(NotificationQuery query, CancellationToken token = default);
    /// <summary>表示可能な通知の既読状態だけを更新する。</summary>
    Task<ArticleMetadata> SetReadAsync(SetArticleReadRequest request, CancellationToken token = default);
    /// <summary>既読に影響させず、ブックマークだけを更新する。</summary>
    Task<ArticleMetadata> SetBookmarkAsync(SetArticleBookmarkRequest request, CancellationToken token = default);
    /// <summary>タグ定義の再利用と記事関連の作成を同時に確定する。</summary>
    Task<ArticleMetadata> AddTagAsync(AddArticleTagRequest request, CancellationToken token = default);
    /// <summary>タグ定義を残し、対象記事との関連だけを解除する。</summary>
    Task<ArticleMetadata> RemoveTagAsync(RemoveArticleTagRequest request, CancellationToken token = default);
}
