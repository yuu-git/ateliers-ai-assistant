using System.Text.Json;
using System.Text.Json.Serialization;
using Ateliers.Ai.Assistant.Application.Notifications;
using Ateliers.Ai.Assistant.Application.Storage;
using Ateliers.Ai.Assistant.Application.WebMonitoring;
using Ateliers.Ai.Assistant.Infrastructure.Conversations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Ateliers.Ai.Assistant.Infrastructure.WebMonitoring;

/// <summary>既存 DB に Migration で追加する記事・実行履歴・通知。同期 SQLite I/O は背景スレッドで実行する。</summary>
public sealed partial class SqliteWebMonitoringStore(IApplicationPaths paths, IAppDiagnostics diagnostics) : IWebMonitoringStore
{
    private readonly SemaphoreSlim _gate = AssistantDatabase.Gate(paths);
    private bool _databaseSeen;
    /// <inheritdoc/>
    public Task DeleteArticleAsync(Guid id, CancellationToken token) => Execute(async db =>
    {
        if (id == Guid.Empty) throw new StorageException("削除対象の記事が不正です。");
        if (db is null) return false;
        await using var transaction = await db.Database.BeginTransactionAsync(token).ConfigureAwait(false);
        var row = await db.Set<ArticleRow>().AsNoTracking().SingleOrDefaultAsync(article => article.Id == id, token).ConfigureAwait(false);
        if (row is null) return false; // 他の操作で削除済みでも成功として扱う。
        var relative = ArticleMarkdownWriter.RelativePath(id);
        if (row.MarkdownPath is not null && row.MarkdownPath != relative) throw new StorageException("保存記事のパスが不正なため削除できません。");
        // SQL・ロックの失敗を先に検出する。ファイル削除に失敗した場合は DB の削除をロールバックする。
        await db.Set<ArticleRow>().Where(article => article.Id == id).ExecuteDeleteAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        try { File.Delete(Path.Combine(paths.LocalDirectory, relative)); }
        catch (DirectoryNotFoundException) { /* ファイルを手動削除済みの場合も記録を削除できる。 */ }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new StorageException("保存 Markdown を削除できませんでした。開いているファイル・アクセス権を確認してください。この記事の記録は保持しました。", exception); }
        // ファイル削除後は中止によるロールバックを避け、この記事の DB 確定までは完了させる。
        try { await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) { throw new StorageException("Markdown 削除後の DB 更新を確定できませんでした。対象の記事を再度削除してください。", exception); }
        return true;
    }, false, token);
    /// <inheritdoc/>
    public Task SaveRunAsync(CollectionRun run, CancellationToken token) => Execute(async db =>
    {
        var row = await db!.Set<RunRow>().FindAsync([run.Id], token).ConfigureAwait(false);
        if (row is null) { row = new RunRow { Id = run.Id, StartedTicks = run.StartedAt.UtcTicks }; db.Add(row); }
        row.Payload = JsonSerializer.Serialize(run, WebJsonContext.Default.CollectionRun);
        await db.SaveChangesAsync(token).ConfigureAwait(false); return true;
    }, true, token);
    /// <inheritdoc/>
    public Task<int> RegisterAsync(IReadOnlyList<FeedArticle> articles, DateTimeOffset fetchedAt, CancellationToken token) => Execute(async db =>
    {
        await using var transaction = await db!.Database.BeginTransactionAsync(token).ConfigureAwait(false);
        var count = 0;
        foreach (var article in articles.DistinctBy(article => article.Key))
        {
            var id = Guid.NewGuid();
            var payload = JsonSerializer.Serialize(article, WebJsonContext.Default.FeedArticle);
            count += await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO WebArticles (Id, Key, Source, Payload, FetchedTicks, Notified) VALUES ({id}, {article.Key}, {article.Source}, {payload}, {fetchedAt.UtcTicks}, {false}) ON CONFLICT(Key) DO NOTHING", token).ConfigureAwait(false);
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return count;
    }, true, token);
    /// <inheritdoc/>
    public Task<IReadOnlyList<CollectedArticle>> PendingAsync(string source, CancellationToken token) => Execute<IReadOnlyList<CollectedArticle>>(async db =>
        db is null ? [] : (await db.Set<ArticleRow>().AsNoTracking().Where(row => row.Source == source && !row.Notified)
            .OrderBy(row => row.FetchedTicks).ToListAsync(token).ConfigureAwait(false)).Select(ToArticle).ToArray(), false, token);
    /// <inheritdoc/>
    public Task SaveArticleAsync(CollectedArticle article, CancellationToken token) => Execute(async db =>
    {
        var row = await db!.Set<ArticleRow>().SingleAsync(row => row.Id == article.Id, token).ConfigureAwait(false);
        if (!row.Notified) { row.Summary = article.Summary; row.Model = article.Model; row.Error = article.Error; }
        await db.SaveChangesAsync(token).ConfigureAwait(false); return true;
    }, false, token);
    /// <inheritdoc/>
    public Task CompleteAsync(CollectedArticle article, CancellationToken token) => Execute(async db =>
    {
        if (article.MarkdownPath != ArticleMarkdownWriter.RelativePath(article.Id) || string.IsNullOrWhiteSpace(article.Summary))
            throw new StorageException("記事の保存結果が不正です。");
        await using var transaction = await db!.Database.BeginTransactionAsync(token).ConfigureAwait(false);
        var row = await db.Set<ArticleRow>().SingleAsync(row => row.Id == article.Id, token).ConfigureAwait(false);
        row.MarkdownPath = article.MarkdownPath; row.Notified = true; row.Error = null; row.Processing = false; row.RetryAfterTicks = null;
        if (!await db.Set<NotificationRow>().AnyAsync(notification => notification.ArticleId == article.Id, token).ConfigureAwait(false))
            db.Add(new NotificationRow { ArticleId = article.Id, CreatedTicks = DateTimeOffset.UtcNow.UtcTicks });
        await db.SaveChangesAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false); return true;
    }, false, token);
    /// <inheritdoc/>
    public Task<IReadOnlyList<CollectionRun>> RunsAsync(CancellationToken token) => Execute<IReadOnlyList<CollectionRun>>(async db =>
        db is null ? [] : (await db.Set<RunRow>().AsNoTracking().OrderByDescending(row => row.StartedTicks).Take(100).ToListAsync(token).ConfigureAwait(false))
            .Select(row => JsonSerializer.Deserialize(row.Payload, WebJsonContext.Default.CollectionRun)!).Select(run =>
                run.FinishedAt is null ? run with { Status = "未完了（実行中、または前回中断）" } : run).ToArray(), false, token);
    /// <inheritdoc/>
    public Task<IReadOnlyList<ArticleNotification>> NotificationsAsync(CancellationToken token) => Execute<IReadOnlyList<ArticleNotification>>(async db =>
    {
        if (db is null) return [];
        // 既存 JSON 内の公開日時も対象にし、全通知から並べた後で100件を選ぶ。
        // julianday は ISO 8601 の UTC/オフセットを解釈する。日時不明は通知作成日時へフォールバックする。
        var order = await db.Database.SqlQueryRaw<NotificationOrder>("""
            SELECT n.ArticleId, n.CreatedTicks
            FROM WebNotifications AS n INNER JOIN WebArticles AS a ON a.Id = n.ArticleId
            ORDER BY COALESCE(julianday(json_extract(a.Payload, '$.PublishedAt')),
                n.CreatedTicks / 864000000000.0 + 1721425.5) DESC,
                n.CreatedTicks DESC, n.ArticleId ASC
            LIMIT 100
            """).ToListAsync(token).ConfigureAwait(false);
        var ids = order.Select(row => row.ArticleId).ToArray();
        var articles = await db.Set<ArticleRow>().AsNoTracking().Where(row => ids.Contains(row.Id))
            .ToDictionaryAsync(row => row.Id, token).ConfigureAwait(false);
        return order.Select(row =>
        {
            var stored = articles[row.ArticleId];
            var article = ToArticle(stored);
            return new ArticleNotification(new NotificationDto(article.Id, new DateTimeOffset(row.CreatedTicks, TimeSpan.Zero),
                NotificationKind.Information, article.Article.Title, article.Summary ?? ""), article.Article.Url, article.MarkdownPath!, article.Article.PublishedAt, article.Model, stored.Source, GitHubRelease: article.Article.GitHubRelease);
        }).ToArray();
    }, false, token);
    /// <summary>段階を保ったまま、API 非依存の再試行スナップショットを復元する。</summary>
    private static CollectedArticle ToArticle(ArticleRow row) => new(row.Id,
        JsonSerializer.Deserialize(row.Payload, WebJsonContext.Default.FeedArticle)!, new DateTimeOffset(row.FetchedTicks, TimeSpan.Zero),
        row.Summary, row.Model, row.MarkdownPath, row.Notified, row.Error, row.Processing,
        row.RetryAfterTicks is { } retry ? new DateTimeOffset(retry, TimeSpan.Zero) : null);
    /// <summary>会話ストアと同じ DB の排他権を使い、欠損・破損時に初期化し直さない。</summary>
    private async Task<T> Execute<T>(Func<AssistantDbContext?, Task<T>> work, bool create, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                bool exists;
                try { exists = (File.GetAttributes(paths.DatabaseFile) & FileAttributes.Directory) == 0; if (!exists) throw new IOException("Database path is a directory."); }
                catch (FileNotFoundException) { exists = false; }
                catch (DirectoryNotFoundException) { exists = false; }
                if (!exists && _databaseSeen) throw new StorageException("使用中の DB が削除・移動されています。アプリを再起動して保存先を確認してください。");
                if (!exists && !create) return await work(null).ConfigureAwait(false);
                if (create) Directory.CreateDirectory(paths.DatabaseDirectory);
                using var processLock = await AssistantDatabase.EnterProcessAsync(paths, token).ConfigureAwait(false);
                var connection = new SqliteConnectionStringBuilder { DataSource = paths.DatabaseFile, ForeignKeys = true,
                    Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 3 };
                await using var db = new AssistantDbContext(new DbContextOptionsBuilder<AssistantDbContext>().UseSqlite(connection.ToString()).Options);
                await db.Database.MigrateAsync(token).ConfigureAwait(false);
                _databaseSeen = true;
                return await work(db).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            diagnostics.Error("web.storage", exception);
            if (exception is StorageException or WebMonitoringException) throw;
            throw new StorageException("Web 収集データを読み書きできませんでした。DB の状態・アクセス権・空き容量を確認してください。", exception);
        }
        finally { _gate.Release(); }
    }
}
/// <summary>公開日時で選んだ最新100件の通知識別子と、共通 DTO 用の通知作成日時。</summary>
internal sealed class NotificationOrder
{
    /// <summary>通知と記事の共通識別子。</summary>
    public Guid ArticleId { get; set; }
    /// <summary>通知作成日時の UTC ticks。</summary>
    public long CreatedTicks { get; set; }
}
/// <summary>永続化用の JSON メタデータを生成する。</summary>
[JsonSerializable(typeof(FeedArticle))]
[JsonSerializable(typeof(CollectionRun))]
internal partial class WebJsonContext : JsonSerializerContext { }
