using System.Text.Json;
using Ateliers.Ai.Assistant.Application.WebMonitoring;
using Ateliers.Ai.Assistant.Application.Storage;
using Ateliers.Ai.Assistant.Desktop.ViewModels;
using Ateliers.Ai.Assistant.Infrastructure.Conversations;
using Ateliers.Ai.Assistant.Infrastructure.Storage;
using Ateliers.Ai.Assistant.Infrastructure.WebMonitoring;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Ateliers.Ai.Assistant.Desktop.Tests;

/// <summary>実 SQLite で100件外の検索、全通知の集計、メタデータの独立性と移行を検証する。</summary>
internal static class NotificationManagementTests
{
    public static async Task QueriesAndCounts()
    {
        using var f = new Fixture(); await f.SeedAsync(132, 130);
        var all = await f.Store.QueryAsync(new());
        Check(all.Items.Count == 100 && all.TotalUnread == 130 && all.UnreadBySource.Count == 3, "unread counts include all notified rows and deleted source, exclude pending");
        Check(all.Items[0].FetchedAt == f.First.AddMinutes(129) && all.Items.All(item => item.Notification.OccurredAt > item.FetchedAt), "first acquisition, not notification creation, controls order");
        var manyUnread = await f.Store.QueryAsync(new(Filter: NotificationViewFilter.Unread));
        Check(manyUnread.Items.Count == 100 && manyUnread.MatchingCount == 130, "unread applies database limit after condition, with complete count");
        await f.SqlAsync("UPDATE WebArticles SET ReadAtTicks = $at WHERE FetchedTicks >= $cut AND Notified = 1", ("$at", DateTimeOffset.UtcNow.UtcTicks), ("$cut", f.First.AddMinutes(30).UtcTicks));
        var unread = await f.Store.QueryAsync(new(Filter: NotificationViewFilter.Unread));
        Check(unread.Items.Count == 30 && unread.TotalUnread == 30 && unread.Items.All(item => item.FetchedAt < f.First.AddMinutes(30)), "unread query reaches entries outside global latest100");
        var old = await f.ArticleAsync(0); await f.Store.SetBookmarkAsync(new(old.Id, true));
        var bookmarks = await f.Store.QueryAsync(new(Filter: NotificationViewFilter.Bookmarked));
        Check(bookmarks.Items.Single().Notification.Id == old.Id && bookmarks.Items[0].ReadAt is null, "old bookmark is queryable and does not mark read");
        await f.Store.AddTagAsync(new(old.Id, " C# "));
        var tagged = await f.Store.QueryAsync(new(f.Sources[0], NotificationViewFilter.Unread, "c#"));
        Check(tagged.Items.Single().Notification.Id == old.Id && tagged.Items[0].Tags!.Single().Name == "C#", "tag/read/source filters apply in database before limit");
        var limited = await f.Store.QueryAsync(new(f.Sources[0]));
        Check(limited.Items.Count < 100 && limited.Items.All(item => item.FetchedAt >= f.First.AddMinutes(30)), "all without tag preserves global latest100 scope");
        await f.SqlAsync("UPDATE WebArticles SET IsBookmarked = 1 WHERE Notified = 1");
        var manyBookmarks = await f.Store.QueryAsync(new(Filter: NotificationViewFilter.Bookmarked));
        Check(manyBookmarks.Items.Count == 100 && manyBookmarks.MatchingCount == 130, "bookmark database query handles more than100 matches");
        var tagId = tagged.Items.Single().Tags!.Single().Id;
        await f.SqlAsync("INSERT OR IGNORE INTO WebArticleTags (ArticleId,TagId) SELECT Id,$tag FROM WebArticles WHERE Notified = 1", ("$tag", tagId));
        var manyTags = await f.Store.QueryAsync(new(TagName: "C#"));
        Check(manyTags.Items.Count == 100 && manyTags.MatchingCount == 130, "tag database query handles more than100 matches");
        var oldUnreadTagged = await f.Store.QueryAsync(new(Filter: NotificationViewFilter.Unread, TagName: "C#"));
        Check(oldUnreadTagged.Items.Count == 30 && oldUnreadTagged.Items.All(item => item.FetchedAt < f.First.AddMinutes(30)), "combined tag/unread reaches older rows instead of filtering latest100");
    }
    public static async Task MetadataAndDelete()
    {
        using var f = new Fixture(); await f.SeedAsync(3, 2);
        var article = await f.ArticleAsync(0); var other = await f.ArticleAsync(1);
        var pending = await f.ArticleAsync(2);
        await Expect<WebMonitoringException>(() => f.Store.SetReadAsync(new(pending.Id, true)));
        var attached = await Task.WhenAll(f.Store.AddTagAsync(new(article.Id, "C#")), f.Store.AddTagAsync(new(article.Id, " c# ")));
        Check(attached.Last().Tags.Count == 1 && attached.Last().ReadAt is null, "concurrent tag reuse is unique and not read");
        await f.Store.AddTagAsync(new(article.Id, "調査"));
        await f.Store.AddTagAsync(new(other.Id, "C#"));
        var bookmarked = await f.Store.SetBookmarkAsync(new(article.Id, true));
        Check(bookmarked.ReadAt is null && bookmarked.Tags.Count == 2, "bookmark keeps unread and tags independent");
        var read = await f.Store.SetReadAsync(new(article.Id, true));
        Check((await f.Store.SetReadAsync(new(article.Id, true))).ReadAt == read.ReadAt, "read update is idempotent");
        var restored = new SqliteWebMonitoringStore(f.Paths, f.Log);
        var after = (await restored.QueryAsync(new(Filter: NotificationViewFilter.Bookmarked))).Items.Single();
        Check(after.ReadAt == read.ReadAt && after.Tags!.Count == 2 && after.IsBookmarked, "restart restores all metadata");
        await f.Store.SetReadAsync(new(article.Id, false));
        await f.Store.RemoveTagAsync(new(article.Id, after.Tags![0].Id));
        Check((await f.Store.QueryAsync(new())).Items.Single(item => item.Notification.Id == article.Id).ReadAt is null, "tag removal does not read");
        await f.Store.DeleteArticleAsync(article.Id, default);
        var page = await f.Store.QueryAsync(new());
        Check(page.Items.Count == 1 && page.TagCatalog.Count == 2 && (await f.Store.RunsAsync(default)).Count == 1, "delete cascades metadata/links, preserves shared and orphan tags and runs");
        await f.Store.RegisterAsync([article.Article], DateTimeOffset.UtcNow, default);
        var fresh = (await f.Store.PendingAsync(article.Article.Source, default)).Single(item => item.Article.Key == article.Article.Key);
        Check(fresh.Id != article.Id && (await f.Store.QueryAsync(new())).TotalUnread == 1, "recollected pending article has new identity and is not counted yet");
        await f.Store.SaveArticleAsync(fresh with { Summary = "再取得", Model = "model" }, default);
        await f.Store.CompleteAsync(fresh with { Summary = "再取得", Model = "model", MarkdownPath = ArticleMarkdownWriter.RelativePath(fresh.Id) }, default);
        var notification = (await f.Store.QueryAsync(new())).Items.Single(item => item.Notification.Id == fresh.Id);
        Check(notification.ReadAt is null && !notification.IsBookmarked && notification.Tags!.Count == 0, "recollected notification starts unread without bookmark or tags");
    }
    public static async Task MigrationAndTagRules()
    {
        using var f = new Fixture(); Directory.CreateDirectory(f.Paths.DatabaseDirectory);
        var id = Guid.NewGuid(); var feed = new FeedArticle("existing", f.Sources[0], "old", "https://example.com/article", "既存記事", f.First.AddDays(-1), "本文", "概要");
        await using (var db = f.Context())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261029034820_WorkerQueueAndSchedule");
            var payload = JsonSerializer.Serialize(feed);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO WebArticles (Id,Key,Source,Payload,FetchedTicks,Summary,Model,MarkdownPath,Notified,Processing) VALUES ({id},{"existing"},{feed.Source},{payload},{f.First.UtcTicks},{"保存済み要約"},{"old-model"},{ArticleMarkdownWriter.RelativePath(id)},{true},{false})");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO WebNotifications (ArticleId,CreatedTicks) VALUES ({id},{f.First.AddDays(1).UtcTicks})");
        }
        var upgraded = (await f.Store.QueryAsync(new())).Items.Single();
        Check(upgraded.Notification.MarkdownBody == "保存済み要約" && upgraded.FetchedAt == f.First && upgraded.ReadAt is null && !upgraded.IsBookmarked && upgraded.Tags!.Count == 0, "old data migrates unread without changing body or first acquisition");
        await using (var db = f.Context())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261029034820_WorkerQueueAndSchedule");
            await db.Database.MigrateAsync();
        }
        Check((await f.Store.QueryAsync(new())).Items.Single().Notification.Id == id, "migration rollback and reapply preserve article identity");
        Check(TagNameRules.Normalize(" c# ").Key == TagNameRules.Normalize("C#").Key && TagNameRules.Normalize("Ｃ＃").Key != TagNameRules.Normalize("C#").Key, "case-insensitive key distinguishes fullwidth");
        Check(TagNameRules.Normalize("Cafe\u0301").Key == TagNameRules.Normalize("Café").Key && TagNameRules.Normalize("AI  News").Name == "AI  News", "NFC and internal whitespace policy");
        foreach (var value in new[] { " ", new string('x', 65), "bad\nname" }) await Expect<WebMonitoringException>(() => Task.FromResult(TagNameRules.Normalize(value)));
        var release = new AppDataPaths(Path.Combine(f.Root, "r"), Path.Combine(f.Root, "l"), AppDataEnvironment.Release);
        var debug = new AppDataPaths(Path.Combine(f.Root, "r"), Path.Combine(f.Root, "l"), AppDataEnvironment.Debug);
        var emptyPaths = f.Paths.DataEnvironment == AppDataEnvironment.Debug ? release : debug;
        Check((await new SqliteWebMonitoringStore(emptyPaths, f.Log).QueryAsync(new())).Items.Count == 0, "metadata data environments remain separate");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Expect<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    internal sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ateliers-notifications-" + Guid.NewGuid().ToString("N"));
        public string[] Sources { get; } = ["https://example.com/a", "https://example.com/b", "https://example.com/deleted"];
        public DateTimeOffset First { get; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public AppDataPaths Paths { get; }
        public FileDiagnosticLog Log { get; }
        public SqliteWebMonitoringStore Store { get; }
        public Fixture() { Paths = new(Path.Combine(Root, "r"), Path.Combine(Root, "l")); Log = new(Paths); Store = new(Paths, Log); }
        public AssistantDbContext Context() => new(new DbContextOptionsBuilder<AssistantDbContext>().UseSqlite($"Data Source={Paths.DatabaseFile};Pooling=False").Options);
        public async Task SeedAsync(int count, int notified)
        {
            await Store.RegisterAsync(Enumerable.Range(0, count).Select(index => new FeedArticle("key-" + index.ToString("D4"), Sources[index % 3], null, "https://example.com/article/" + index, "記事 " + index, First.AddDays(-2), "本文", "概要")).ToArray(), First, default);
            await Store.SaveRunAsync(new(Guid.NewGuid(), First, First.AddHours(1), "取得完了"), default);
            await using var connection = new SqliteConnection($"Data Source={Paths.DatabaseFile};Pooling=False;Foreign Keys=True"); await connection.OpenAsync();
            var rows = new List<(Guid Id, int Index)>();
            using (var command = connection.CreateCommand()) { command.CommandText = "SELECT Id,Key FROM WebArticles"; using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) rows.Add((Guid.Parse(reader.GetString(0)), int.Parse(reader.GetString(1)[4..]))); }
            await using var transaction = await connection.BeginTransactionAsync();
            foreach (var row in rows)
            {
                using var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = "UPDATE WebArticles SET FetchedTicks=$f,Summary='要約',Model='model',MarkdownPath=$p,Notified=$n WHERE Id=$id; INSERT INTO WebNotifications (ArticleId,CreatedTicks) SELECT $id,$c WHERE $n=1";
                command.Parameters.AddWithValue("$id", row.Id); command.Parameters.AddWithValue("$f", First.AddMinutes(row.Index).UtcTicks);
                command.Parameters.AddWithValue("$p", ArticleMarkdownWriter.RelativePath(row.Id)); command.Parameters.AddWithValue("$n", row.Index < notified);
                command.Parameters.AddWithValue("$c", First.AddDays(2).UtcTicks); await command.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }
        public async Task SqlAsync(string sql, params (string Name, object Value)[] values)
        { await using var connection = new SqliteConnection($"Data Source={Paths.DatabaseFile};Pooling=False"); await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = sql; foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value); await command.ExecuteNonQueryAsync(); }
        public async Task<CollectedArticle> ArticleAsync(int index)
        { var pending = await Store.PendingAsync(Sources[index % 3], default); var key = "key-" + index.ToString("D4"); if (pending.FirstOrDefault(row => row.Article.Key == key) is { } found) return found;
          await using var connection = new SqliteConnection($"Data Source={Paths.DatabaseFile};Pooling=False"); await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "SELECT Id,Payload FROM WebArticles WHERE Key=$key"; command.Parameters.AddWithValue("$key", key); using var reader = await command.ExecuteReaderAsync(); await reader.ReadAsync(); return new(Guid.Parse(reader.GetString(0)), JsonSerializer.Deserialize<FeedArticle>(reader.GetString(1))!, First.AddMinutes(index)); }
        public void Dispose()
        { var path = Path.GetFullPath(Root); if (Path.GetDirectoryName(path) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(path).StartsWith("ateliers-notifications-")) throw new InvalidOperationException("Unsafe test path"); if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
