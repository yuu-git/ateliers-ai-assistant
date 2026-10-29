#if DEBUG
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Ateliers.Ai.Assistant.Application.Chat;
using Ateliers.Ai.Assistant.Application.Models;
using Ateliers.Ai.Assistant.Application.WebMonitoring;
using Ateliers.Ai.Assistant.Core.Chat;

namespace Ateliers.Ai.Assistant.Worker;

/// <summary>実サイト・Ollama・通常 AppData に接続しないプロセス検証用。</summary>
internal sealed class VerificationAdapters : IFeedSource, IChatService, IModelControlService
{
    private bool _loaded;
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
