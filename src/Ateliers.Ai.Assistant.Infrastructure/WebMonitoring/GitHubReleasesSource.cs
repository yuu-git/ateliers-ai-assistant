using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ateliers.Ai.Assistant.Application.WebMonitoring;
using Ateliers.Ai.Assistant.Application.Worker;

namespace Ateliers.Ai.Assistant.Infrastructure.WebMonitoring;

/// <summary>公開 GitHub の1ページだけを、認証・資産ダウンロード・HTML取得なしで読む。</summary>
public sealed class GitHubReleasesSource(HttpClient client, TimeProvider? clock = null) : IContentSource
{
    /// <summary>展開後の応答サイズ上限。記事本文は別途12,000文字に制限する。</summary>
    public const int MaximumBytes = 8 * 1024 * 1024;
    /// <inheritdoc/>
    public async Task<IReadOnlyList<FeedArticle>> FetchAsync(SourceConfiguration source, CancellationToken token)
    {
        if (source.Kind != ContentSourceKind.GitHubReleases) throw new WebMonitoringException("GitHub Releases の情報源を指定してください。");
        var repository = ContentSourceIdentity.Repository(source.Url);
        var original = ContentSourceIdentity.Normalize(source.Kind, source.Url);
        var uri = new Uri(original + "?per_page=100&page=1");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            for (var redirect = 0; redirect <= 5; redirect++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("Ateliers-Ai-Assistant/5.0");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is null || redirect == 5)
                        throw new GitHubFetchException("GitHub API のリダイレクト応答が不正です。");
                    var next = new Uri(uri, response.Headers.Location);
                    if (next.Scheme != "https" || next.Host != "api.github.com" || !next.IsDefaultPort || next.UserInfo.Length != 0 || next.Fragment.Length != 0)
                        throw new GitHubFetchException("GitHub API 以外へのリダイレクトを拒否しました。");
                    uri = new UriBuilder(next) { Query = "per_page=100&page=1" }.Uri; continue;
                }
                if (!response.IsSuccessStatusCode) throw await FailureAsync(response, deadline.Token).ConfigureAwait(false);
                if (response.Content.Headers.ContentLength > MaximumBytes) throw new GitHubFetchException("GitHub API の応答が上限（8 MiB）を超えています。");
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                using var buffer = new MemoryStream(); var block = new byte[16384]; int read;
                while ((read = await stream.ReadAsync(block, deadline.Token).ConfigureAwait(false)) != 0)
                {
                    if (buffer.Length + read > MaximumBytes) throw new GitHubFetchException("GitHub API の応答が上限（8 MiB）を超えています。");
                    await buffer.WriteAsync(block.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
                }
                var releases = JsonSerializer.Deserialize(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), GitHubJsonContext.Default.GitHubReleaseResponseArray)
                    ?? throw new JsonException();
                if (releases.Length > 100 || releases.Any(item => item is null || item.Id <= 0 || item.Draft is null || item.Prerelease is null || string.IsNullOrWhiteSpace(item.TagName) || item.TagName.Length > 4096))
                    throw new JsonException();
                return FeedSelection.Latest(releases.Where(item => item.Draft == false && (source.IncludePrereleases || item.Prerelease == false))
                    .Select(item =>
                    {
                        var title = string.IsNullOrWhiteSpace(item.Name) ? item.TagName! : item.Name;
                        var content = item.Body ?? "";
                        var externalId = "github-release:" + item.Id.ToString(CultureInfo.InvariantCulture);
                        var url = SafeArticleUrl(item.HtmlUrl);
                        return new FeedArticle(FeedIdentity.Key("github-releases:" + repository, externalId, null, "", null), original, externalId,
                            url, Limit(title, 1000), item.PublishedAt?.ToUniversalTime(), Limit(content, 12000),
                            "GitHub Releases API の本文（リンク先・添付資産は未取得）" + (content.Length > 12000 ? "・先頭12,000文字" : ""),
                            new(item.Id, repository, item.TagName!, item.Draft!.Value, item.Prerelease!.Value));
                    }).ToArray());
            }
            throw new GitHubFetchException("GitHub API を取得できませんでした。");
        }
        catch (JsonException) { throw new GitHubFetchException("GitHub API の JSON またはリリース情報が不正です。"); }
        catch (HttpRequestException) { throw new GitHubFetchException("GitHub API に接続できません。ネットワークを確認してください。"); }
        catch (IOException) { throw new GitHubFetchException("GitHub API の応答を読み取れませんでした。次回取得で再試行してください。"); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new GitHubFetchException("GitHub API の取得がタイムアウトしました（30秒）。"); }
    }
    /// <summary>外部の応答本文をログ・画面に流さず、HTTPと待機期限だけを分類する。</summary>
    private async Task<GitHubFetchException> FailureAsync(HttpResponseMessage response, CancellationToken token)
    {
        var limited = response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.Forbidden &&
            (response.Headers.RetryAfter is not null || response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0"));
        if (!limited && response.StatusCode == HttpStatusCode.Forbidden && response.Content.Headers.ContentLength is not > 4096)
        {
            // 二次制限はヘッダーがない場合もある。理由の判定だけに使い本文は公開・記録しない。
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var bytes = new byte[4097]; var length = 0;
            while (length < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(length), token).ConfigureAwait(false);
                if (read == 0) break; length += read;
            }
            try
            {
                using var document = JsonDocument.Parse(bytes.AsMemory(0, length));
                limited = length <= 4096 && document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("message", out var message) &&
                    message.ValueKind == JsonValueKind.String && message.GetString()!.Contains("rate limit", StringComparison.OrdinalIgnoreCase);
            }
            catch (JsonException) { }
        }
        if (limited)
        {
            var now = (clock ?? TimeProvider.System).GetUtcNow(); var retry = now.AddMinutes(5);
            if (response.Headers.RetryAfter?.Date is { } date) retry = date;
            else if (response.Headers.RetryAfter?.Delta is { } delay) retry = now + delay;
            if (response.Headers.TryGetValues("X-RateLimit-Reset", out var reset) && long.TryParse(reset.FirstOrDefault(), out var seconds) && seconds is >= 0 and <= 253402300799)
            {
                var resetAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
                if (response.Headers.RetryAfter is null || resetAt > retry) retry = resetAt;
            }
            if (retry < now.AddMinutes(1)) retry = now.AddMinutes(1);
            return new("GitHub API のレート制限です。解除予定まで GitHub 情報源の取得を待機します。", retry);
        }
        return new(response.StatusCode switch
        {
            HttpStatusCode.NotFound => "GitHub リポジトリが見つからないか、公開されていません（HTTP 404）。owner/repository を確認してください。",
            HttpStatusCode.Forbidden => "GitHub API へのアクセスが拒否されました（HTTP 403）。公開範囲・アクセス制限を確認してください。",
            _ => $"GitHub API の取得に失敗しました（HTTP {(int)response.StatusCode}）。"
        });
    }
    /// <summary>記事を開く操作には GitHub の HTTPS URL のみを渡す。</summary>
    private static string? SafeArticleUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.Host == "github.com" && uri.IsDefaultPort && uri.UserInfo.Length == 0 ? uri.AbsoluteUri : null;
    /// <summary>外部文字列の上限。</summary>
    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];
}

/// <summary>公式 API の必要な応答項目。取得タグは記事の手動タグと混在させない。</summary>
internal sealed record GitHubReleaseResponse(long Id, string? Name, string? Body, string? HtmlUrl, DateTimeOffset? PublishedAt,
    string? TagName, bool? Draft, bool? Prerelease);
/// <summary>JSON の名前・型を静的に定義する。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GitHubReleaseResponse[]))]
internal partial class GitHubJsonContext : JsonSerializerContext { }

/// <summary>実装済みの2種類だけを振り分ける。汎用プラグインや UI 依存を持たない。</summary>
public sealed class ContentSourceRouter(IFeedSource rss, IContentSource github) : IContentSource
{
    /// <inheritdoc/>
    public Task<IReadOnlyList<FeedArticle>> FetchAsync(SourceConfiguration source, CancellationToken token) => source.Kind switch
    {
        ContentSourceKind.RssAtom => rss.FetchAsync(source.Url, token),
        ContentSourceKind.GitHubReleases => github.FetchAsync(source, token),
        _ => throw new WebMonitoringException("情報源の種別が不正です。")
    };
}
