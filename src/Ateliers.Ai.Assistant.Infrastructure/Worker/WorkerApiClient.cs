using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Ateliers.Ai.Assistant.Application.WebMonitoring;
using Ateliers.Ai.Assistant.Application.Worker;

namespace Ateliers.Ai.Assistant.Infrastructure.Worker;

/// <summary>接続情報を毎回読み、再起動時のポート・認証キー更新を追従する UI 側アダプター。</summary>
public sealed partial class WorkerApiClient(HttpClient client, WorkerConnectionFiles connection) : IWorkerClient, IWebMonitoringReader, IWebCollectionOperations, INotificationManagement, IWorkerAiControl, IWorkerPresenceProbe
{
    /// <inheritdoc/>
    public Task<WorkerPresence> GetPresenceAsync(CancellationToken token = default) => Task.Run(() => connection.InspectPresence(), token);
    /// <inheritdoc/>
    public Task<WorkerStatus> GetStatusAsync(CancellationToken token = default) => SendAsync(HttpMethod.Get, "status", null, WorkerJsonContext.Default.WorkerStatus, token);
    /// <inheritdoc/>
    public Task<WorkerConfiguration> SaveConfigurationAsync(WorkerConfiguration configuration, CancellationToken token = default) =>
        SendAsync(HttpMethod.Put, "configuration", JsonContent.Create(configuration, WorkerJsonContext.Default.WorkerConfiguration), WorkerJsonContext.Default.WorkerConfiguration, token);
    /// <inheritdoc/>
    public Task<FetchAccepted> FetchAsync(FetchRequest request, CancellationToken token = default) =>
        SendAsync(HttpMethod.Post, "fetch", JsonContent.Create(request, WorkerJsonContext.Default.FetchRequest), WorkerJsonContext.Default.FetchAccepted, token);
    /// <inheritdoc/>
    public Task<IReadOnlyList<CollectionRun>> GetRunsAsync(CancellationToken token = default) =>
        SendAsync(HttpMethod.Get, "runs", null, WorkerJsonContext.Default.IReadOnlyListCollectionRun, token);
    /// <inheritdoc/>
    public Task<IReadOnlyList<ArticleNotification>> GetNotificationsAsync(CancellationToken token = default) =>
        SendAsync(HttpMethod.Get, "notifications", null, WorkerJsonContext.Default.IReadOnlyListArticleNotification, token);
    /// <inheritdoc/>
    public Task<ArticleDeletionResult> DeleteArticlesAsync(IReadOnlyList<Guid> ids, CancellationToken token = default) =>
        SendAsync(HttpMethod.Post, "articles/delete", JsonContent.Create(new DeleteArticlesRequest(ids), WorkerJsonContext.Default.DeleteArticlesRequest), WorkerJsonContext.Default.ArticleDeletionResult, token);
    /// <inheritdoc/>
    public Task<IReadOnlyList<CollectionRun>> RunsAsync(CancellationToken token) => GetRunsAsync(token);
    /// <inheritdoc/>
    public Task<IReadOnlyList<ArticleNotification>> NotificationsAsync(CancellationToken token) => GetNotificationsAsync(token);
    /// <inheritdoc/>
    public Task<ArticleDeletionResult> DeleteAsync(IReadOnlyList<Guid> ids, CancellationToken token) => DeleteArticlesAsync(ids, token);
    /// <summary>旧表示 ViewModel 用の互換操作。情報源設定を UI の JSON へ保存しない。</summary>
    public async Task CollectAsync(string url, string? model, IProgress<string>? progress, CancellationToken token)
    {
        var status = await GetStatusAsync(token).ConfigureAwait(false);
        var source = status.Configuration.Sources.SingleOrDefault(source => !source.Deleted && source.Url == FeedIdentity.Source(url))
            ?? throw new WorkerException("Worker の情報源設定から手動取得を実行してください。");
        await FetchAsync(new(source.Id, Guid.NewGuid()), token).ConfigureAwait(false);
        progress?.Report("取得要求を受け付けました。要約は独立して処理します。一覧を更新して状態を確認してください。");
    }
    /// <summary>利用者の明示操作による Worker 停止。UI の通常終了では呼び出さない。</summary>
    public Task<FetchAccepted> StopAsync(CancellationToken token = default) => SendAsync(HttpMethod.Post, "shutdown", JsonContent.Create(new FetchRequest(Guid.Empty, Guid.NewGuid()), WorkerJsonContext.Default.FetchRequest), WorkerJsonContext.Default.FetchAccepted, token);
    private async Task<T> SendAsync<T>(HttpMethod method, string route, HttpContent? content, JsonTypeInfo<T> info, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(route == "articles/delete" ? 30 : 8));
        try
        {
            var endpoint = await connection.ReadAsync(deadline.Token).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, new Uri(new Uri(endpoint.Address), "api/v1/" + route)) { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new WorkerException("Worker の認証に失敗しました。Worker を再起動してください。", connectionFailure: true);
            if (response.StatusCode == HttpStatusCode.Conflict) throw new WorkerException("別の画面で設定または状態が変わりました。一覧を更新して再試行してください。");
            if (!response.IsSuccessStatusCode) throw new WorkerException("Worker が操作を完了できませんでした。入力値とログを確認してください。");
            if (response.Content.Headers.ContentLength > 8 * 1024 * 1024) throw new WorkerException("Worker 応答が上限を超えています。");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var block = new byte[16384]; int read;
            while ((read = await stream.ReadAsync(block, deadline.Token).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + read > 8 * 1024 * 1024) throw new WorkerException("Worker 応答が上限を超えています。");
                buffer.Write(block, 0, read);
            }
            buffer.Position = 0;
            return await JsonSerializer.DeserializeAsync(buffer, info, deadline.Token).ConfigureAwait(false) ?? throw new WorkerException("Worker の応答が不正です。");
        }
        catch (OperationCanceledException exception) when (!token.IsCancellationRequested) { throw new WorkerException("Worker の応答がタイムアウトしました。状態を更新してから再試行してください。", exception, true); }
        catch (HttpRequestException exception) { throw new WorkerException("Worker に接続できません。設定画面で起動状態を確認してください。チャットは引き続き利用できます。", exception, true); }
        catch (JsonException exception) { throw new WorkerException("Worker の応答を読めません。UI と Worker を同じバージョンで起動してください。", exception, true); }
    }
}
