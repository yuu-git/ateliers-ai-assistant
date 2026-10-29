using Ateliers.Ai.Assistant.Application.Notifications;
using Ateliers.Ai.Assistant.Application.WebMonitoring;
using Ateliers.Ai.Assistant.Infrastructure.Conversations;
using Microsoft.EntityFrameworkCore;

namespace Ateliers.Ai.Assistant.Infrastructure.WebMonitoring;

/// <summary>表示可能な通知だけの検索・全件集計と、記事メタデータの更新。</summary>
public sealed partial class SqliteWebMonitoringStore : INotificationManagement
{
    /// <inheritdoc/>
    public Task<NotificationPage> QueryAsync(NotificationQuery query, CancellationToken token = default) => Execute(async db =>
    {
        if (query is null || !Enum.IsDefined(query.Filter) || query.FeedSourceUrl?.Length > 4096) throw new WebMonitoringException("通知の検索条件が不正です。");
        var tagKey = string.IsNullOrWhiteSpace(query.TagName) ? null : TagNameRules.Normalize(query.TagName).Key;
        if (db is null) return new NotificationPage([], 0, [], 0, []);
        var visible = db.Set<ArticleRow>().AsNoTracking().Where(article => article.Notified && db.Set<NotificationRow>().Any(notification => notification.ArticleId == article.Id));
        var unreadCounts = await visible.Where(article => article.ReadAtTicks == null).GroupBy(article => article.Source)
            .Select(group => new SourceUnreadCount(group.Key, group.Count())).ToListAsync(token).ConfigureAwait(false);
        IQueryable<ArticleRow> matching = visible;
        // 承認済み仕様: すべて・タグなしだけは全体最新100件が起点。その他は全通知へ条件を適用する。
        if (query.Filter == NotificationViewFilter.All && tagKey is null)
            matching = matching.OrderByDescending(article => article.FetchedTicks).ThenBy(article => article.Id).Take(100);
        if (query.FeedSourceUrl is not null) matching = matching.Where(article => article.Source == query.FeedSourceUrl);
        if (query.Filter == NotificationViewFilter.Unread) matching = matching.Where(article => article.ReadAtTicks == null);
        if (query.Filter == NotificationViewFilter.Bookmarked) matching = matching.Where(article => article.IsBookmarked);
        if (tagKey is not null) matching = matching.Where(article => db.Set<ArticleTagRow>().Any(link => link.ArticleId == article.Id &&
            db.Set<TagRow>().Any(tag => tag.Id == link.TagId && tag.NormalizedName == tagKey)));
        var count = await matching.CountAsync(token).ConfigureAwait(false);
        var rows = await matching.OrderByDescending(article => article.FetchedTicks).ThenBy(article => article.Id).Take(100).ToListAsync(token).ConfigureAwait(false);
        var ids = rows.Select(article => article.Id).ToArray();
        var notifications = await db.Set<NotificationRow>().AsNoTracking().Where(notification => ids.Contains(notification.ArticleId)).ToDictionaryAsync(notification => notification.ArticleId, token).ConfigureAwait(false);
        var links = await (from link in db.Set<ArticleTagRow>().AsNoTracking()
                           join tag in db.Set<TagRow>().AsNoTracking() on link.TagId equals tag.Id
                           where ids.Contains(link.ArticleId)
                           orderby tag.NormalizedName
                           select new { link.ArticleId, tag.Id, tag.Name }).ToListAsync(token).ConfigureAwait(false);
        var tagLookup = links.ToLookup(link => link.ArticleId, link => new ArticleTag(link.Id, link.Name));
        // 候補は最大500件。候補外もタグ名の手入力で検索・再利用可能。
        var catalog = await db.Set<TagRow>().AsNoTracking().OrderBy(tag => tag.NormalizedName).Take(500).Select(tag => new ArticleTag(tag.Id, tag.Name)).ToListAsync(token).ConfigureAwait(false);
        var items = rows.Select(row =>
        {
            var article = ToArticle(row);
            return new ArticleNotification(new(row.Id, new DateTimeOffset(notifications[row.Id].CreatedTicks, TimeSpan.Zero), NotificationKind.Information, article.Article.Title, row.Summary ?? ""),
                article.Article.Url, row.MarkdownPath!, article.Article.PublishedAt, row.Model, row.Source,
                new(row.FetchedTicks, TimeSpan.Zero), row.ReadAtTicks is { } at ? new DateTimeOffset(at, TimeSpan.Zero) : null, row.IsBookmarked, tagLookup[row.Id].ToArray());
        }).ToArray();
        return new NotificationPage(items, unreadCounts.Sum(item => item.Count), unreadCounts, count, catalog);
    }, false, token);

    /// <inheritdoc/>
    public Task<ArticleMetadata> SetReadAsync(SetArticleReadRequest request, CancellationToken token = default) => MutateAsync(request.ArticleId, async (db, row) =>
    { row.ReadAtTicks = request.IsRead ? row.ReadAtTicks ?? DateTimeOffset.UtcNow.UtcTicks : null; await Task.CompletedTask; }, token);
    /// <inheritdoc/>
    public Task<ArticleMetadata> SetBookmarkAsync(SetArticleBookmarkRequest request, CancellationToken token = default) => MutateAsync(request.ArticleId, async (db, row) =>
    { row.IsBookmarked = request.IsBookmarked; await Task.CompletedTask; }, token);
    /// <inheritdoc/>
    public Task<ArticleMetadata> AddTagAsync(AddArticleTagRequest request, CancellationToken token = default)
    {
        var normalized = TagNameRules.Normalize(request.Name);
        return MutateAsync(request.ArticleId, async (db, row) =>
        {
            var tag = await db.Set<TagRow>().SingleOrDefaultAsync(item => item.NormalizedName == normalized.Key, token).ConfigureAwait(false);
            if (tag is null) { tag = new() { Id = Guid.NewGuid(), Name = normalized.Name, NormalizedName = normalized.Key }; db.Add(tag); }
            if (!await db.Set<ArticleTagRow>().AnyAsync(link => link.ArticleId == row.Id && link.TagId == tag.Id, token).ConfigureAwait(false))
                db.Add(new ArticleTagRow { ArticleId = row.Id, TagId = tag.Id });
        }, token);
    }
    /// <inheritdoc/>
    public Task<ArticleMetadata> RemoveTagAsync(RemoveArticleTagRequest request, CancellationToken token = default) => MutateAsync(request.ArticleId, async (db, row) =>
    {
        if (request.TagId == Guid.Empty) throw new WebMonitoringException("タグが選択されていません。");
        await db.Set<ArticleTagRow>().Where(link => link.ArticleId == row.Id && link.TagId == request.TagId).ExecuteDeleteAsync(token).ConfigureAwait(false);
    }, token);

    /// <summary>記事削除と排他されたトランザクションで、関係のない状態を変更せず確定する。</summary>
    private Task<ArticleMetadata> MutateAsync(Guid id, Func<AssistantDbContext, ArticleRow, Task> change, CancellationToken token) => Execute(async db =>
    {
        if (id == Guid.Empty || db is null) throw new WebMonitoringException("通知が見つかりません。再取得してください。");
        await using var transaction = await db.Database.BeginTransactionAsync(token).ConfigureAwait(false);
        var row = await db.Set<ArticleRow>().SingleOrDefaultAsync(article => article.Id == id && article.Notified &&
            db.Set<NotificationRow>().Any(notification => notification.ArticleId == id), token).ConfigureAwait(false)
            ?? throw new WebMonitoringException("通知が削除されています。再取得してください。");
        await change(db, row).ConfigureAwait(false);
        await db.SaveChangesAsync(token).ConfigureAwait(false);
        var tags = await (from link in db.Set<ArticleTagRow>().AsNoTracking() join tag in db.Set<TagRow>().AsNoTracking() on link.TagId equals tag.Id
                          where link.ArticleId == id orderby tag.NormalizedName select new ArticleTag(tag.Id, tag.Name)).ToListAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return new ArticleMetadata(id, row.ReadAtTicks is { } at ? new DateTimeOffset(at, TimeSpan.Zero) : null, row.IsBookmarked, tags);
    }, false, token);
}
