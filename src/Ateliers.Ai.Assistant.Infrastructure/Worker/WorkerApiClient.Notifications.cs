using System.Net.Http.Json;
using Ateliers.Ai.Assistant.Application.WebMonitoring;
using Ateliers.Ai.Assistant.Application.Worker;

namespace Ateliers.Ai.Assistant.Infrastructure.Worker;

/// <summary>既存の認証・期限・サイズ制限を再利用する通知管理 API。</summary>
public sealed partial class WorkerApiClient
{
    /// <inheritdoc/>
    public Task<NotificationPage> QueryAsync(NotificationQuery query, CancellationToken token = default) =>
        SendAsync(HttpMethod.Post, "notifications/query", JsonContent.Create(query, WorkerJsonContext.Default.NotificationQuery), WorkerJsonContext.Default.NotificationPage, token);
    /// <inheritdoc/>
    public Task<ArticleMetadata> SetReadAsync(SetArticleReadRequest request, CancellationToken token = default) =>
        SendAsync(HttpMethod.Post, "notifications/read", JsonContent.Create(request, WorkerJsonContext.Default.SetArticleReadRequest), WorkerJsonContext.Default.ArticleMetadata, token);
    /// <inheritdoc/>
    public Task<ArticleMetadata> SetBookmarkAsync(SetArticleBookmarkRequest request, CancellationToken token = default) =>
        SendAsync(HttpMethod.Post, "notifications/bookmark", JsonContent.Create(request, WorkerJsonContext.Default.SetArticleBookmarkRequest), WorkerJsonContext.Default.ArticleMetadata, token);
    /// <inheritdoc/>
    public Task<ArticleMetadata> AddTagAsync(AddArticleTagRequest request, CancellationToken token = default) =>
        SendAsync(HttpMethod.Post, "notifications/tags/add", JsonContent.Create(request, WorkerJsonContext.Default.AddArticleTagRequest), WorkerJsonContext.Default.ArticleMetadata, token);
    /// <inheritdoc/>
    public Task<ArticleMetadata> RemoveTagAsync(RemoveArticleTagRequest request, CancellationToken token = default) =>
        SendAsync(HttpMethod.Post, "notifications/tags/remove", JsonContent.Create(request, WorkerJsonContext.Default.RemoveArticleTagRequest), WorkerJsonContext.Default.ArticleMetadata, token);
    /// <inheritdoc/>
    public Task<WorkerConfiguration> SetAiPausedAsync(bool paused, CancellationToken token = default) =>
        SendAsync(HttpMethod.Post, "ai/pause", JsonContent.Create(new AiPauseRequest(paused), WorkerJsonContext.Default.AiPauseRequest), WorkerJsonContext.Default.WorkerConfiguration, token);
}
