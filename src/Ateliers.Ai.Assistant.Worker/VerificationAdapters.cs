#if DEBUG
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Ateliers.Ai.Assistant.Application.Chat;
using Ateliers.Ai.Assistant.Application.Models;
using Ateliers.Ai.Assistant.Application.WebMonitoring;
using Ateliers.Ai.Assistant.Core.Chat;
using Ateliers.Ai.Assistant.Application.Worker;
using Ateliers.Ai.Assistant.Infrastructure.WebMonitoring;

namespace Ateliers.Ai.Assistant.Worker;

/// <summary>実サイト・Ollama・通常 AppData に接続しないプロセス検証用。</summary>
internal sealed class VerificationAdapters : IFeedSource, IChatService, IModelControlService
{
    private bool _loaded;
    /// <summary>GitHub は実アダプターへ偽 HTTP を渡し、独立プロセスでも外部通信しない。</summary>
    async Task<IReadOnlyList<FeedArticle>> IContentSource.FetchAsync(SourceConfiguration source, CancellationToken token)
    {
        if (source.Kind == ContentSourceKind.RssAtom) return await FetchAsync(source.Url, token);
        using var client = new HttpClient(new GitHubHandler());
        return await new GitHubReleasesSource(client).FetchAsync(source, token);
    }
    /// <summary>HTTP/JSON解析から通常の GitHub 経路を検証する。</summary>
    private sealed class GitHubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host != "api.github.com") throw new InvalidOperationException("Unexpected verification host");
            var data = Enumerable.Range(1, 12).Select(id => new { id, name = "GitHub 検証 " + id, body = "検証用のリリースノート", tag_name = "v" + id,
                html_url = "https://github.com/verification/project/releases/tag/v" + id, published_at = DateTimeOffset.UnixEpoch.AddDays(id), draft = false, prerelease = false }).ToArray();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(data)) });
        }
    }
    /// <inheritdoc/>
    public Task<IReadOnlyList<FeedArticle>> FetchAsync(string url, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FeedArticle>>(
        Enumerable.Range(0, 12).Select(index => new FeedArticle(FeedIdentity.Key(url, "test-" + index, null, "", null), url, "test-" + index,
            "https://example.com/" + index, "Worker 検証 " + index, DateTimeOffset.UnixEpoch.AddDays(index), "検証用の概要です。", "フィードの概要のみ")).ToArray());
    /// <inheritdoc/>
    public Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(["verification-model"]);
    /// <inheritdoc/>
    public async IAsyncEnumerable<string> StreamReplyAsync(string model, IReadOnlyList<ChatMessage> messages, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    { await Task.Delay(200, cancellationToken); yield return "# 要約\n\n検証用の日本語要約です。"; }
    /// <inheritdoc/>
    public Task<IReadOnlyList<LoadedModel>> GetLoadedModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LoadedModel>>(
        _loaded ? [new("verification-model", "digest", 1, 1, "Q4", null)] : []);
    /// <inheritdoc/>
    public Task LoadModelAsync(string model, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); _loaded = true; return Task.CompletedTask; }
    /// <inheritdoc/>
    public Task UnloadModelAsync(string model, CancellationToken cancellationToken = default) { _loaded = false; return Task.CompletedTask; }
}
/// <summary>実サインインや長時間待機なしに予定の復旧を検証する Debug 専用時計。</summary>
internal sealed class VerificationClock(DateTimeOffset epoch) : TimeProvider
{
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    /// <inheritdoc/>
    public override DateTimeOffset GetUtcNow() => epoch.AddTicks(((_started - epoch).Ticks + _elapsed.Elapsed.Ticks) * 60);
}
#endif
