using System.Text.Json;
using Ateliers.Ai.Assistant.Application.WebMonitoring;
using Ateliers.Ai.Assistant.Application.Worker;
using Ateliers.Ai.Assistant.Infrastructure.Worker;
using Microsoft.EntityFrameworkCore;

namespace Ateliers.Ai.Assistant.Infrastructure.WebMonitoring;

/// <summary>取得済み記事の永続キューと巡回予定を既存 DB に追加する。</summary>
public sealed partial class SqliteWebMonitoringStore : IBackgroundArticleStore
{
    /// <inheritdoc/>
    public Task<int> PendingCountAsync(IReadOnlyList<string> sources, CancellationToken token) => Execute(async db => db is null ? 0 :
        await db.Set<ArticleRow>().CountAsync(row => !row.Notified && sources.Contains(row.Source), token).ConfigureAwait(false), false, token);
    /// <inheritdoc/>
    public Task<CollectedArticle?> NextPendingAsync(string source, DateTimeOffset now, CancellationToken token) => Execute(async db =>
    {
        var row = db is null ? null : await db.Set<ArticleRow>().AsNoTracking().Where(row => row.Source == source && !row.Notified && !row.Processing &&
            (row.RetryAfterTicks == null || row.RetryAfterTicks <= now.UtcTicks)).OrderBy(row => row.FetchedTicks).FirstOrDefaultAsync(token).ConfigureAwait(false);
        return row is null ? null : ToArticle(row);
    }, false, token);
    /// <inheritdoc/>
    public Task<bool> TryBeginAsync(Guid id, CancellationToken token) => Execute(async db =>
        db is not null && await db.Set<ArticleRow>().Where(row => row.Id == id && !row.Notified && !row.Processing)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Processing, true), token).ConfigureAwait(false) == 1, false, token);
    /// <inheritdoc/>
    public Task EndAttemptAsync(Guid id, string? error, DateTimeOffset? retryAfter, CancellationToken token) => Execute(async db =>
    {
        if (db is not null) await db.Set<ArticleRow>().Where(row => row.Id == id && !row.Notified).ExecuteUpdateAsync(update =>
            update.SetProperty(row => row.Processing, false).SetProperty(row => row.Error, error)
                .SetProperty(row => row.RetryAfterTicks, retryAfter == null ? (long?)null : retryAfter.Value.UtcTicks), token).ConfigureAwait(false);
        return true;
    }, false, token);
    /// <inheritdoc/>
    public Task RecoverAsync(CancellationToken token) => Execute(async db =>
    {
        if (db is not null) await db.Set<ArticleRow>().Where(row => row.Processing && !row.Notified)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Processing, false).SetProperty(row => row.RetryAfterTicks, (long?)null), token).ConfigureAwait(false);
        return true;
    }, false, token);
    /// <inheritdoc/>
    public Task<CollectionRun?> FindRunAsync(Guid id, CancellationToken token) => Execute(async db =>
    {
        var row = db is null ? null : await db.Set<RunRow>().AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, token).ConfigureAwait(false);
        return row is null ? null : JsonSerializer.Deserialize(row.Payload, WebJsonContext.Default.CollectionRun);
    }, false, token);
    /// <summary>Worker が情報源の復旧に使う予定を取得する。</summary>
    public Task<IReadOnlyList<SourceState>> LoadSourceStatesAsync(CancellationToken token) => Execute<IReadOnlyList<SourceState>>(async db =>
        db is null ? [] : (await db.Set<WorkerSourceStateRow>().AsNoTracking().ToListAsync(token).ConfigureAwait(false))
            .Select(row => JsonSerializer.Deserialize(row.Payload, WorkerJsonContext.Default.SourceState)!).ToArray(), false, token);
    /// <summary>同じ情報源の実行状態・予定を置き換える。</summary>
    public Task SaveSourceStateAsync(SourceState state, CancellationToken token) => Execute(async db =>
    {
        var row = await db!.Set<WorkerSourceStateRow>().FindAsync([state.Id], token).ConfigureAwait(false);
        if (row is null) { row = new WorkerSourceStateRow { Id = state.Id }; db.Add(row); }
        row.Payload = JsonSerializer.Serialize(state, WorkerJsonContext.Default.SourceState);
        await db.SaveChangesAsync(token).ConfigureAwait(false); return true;
    }, true, token);
}
