using System.Text.RegularExpressions;
using Ateliers.Ai.Assistant.Application.Worker;

namespace Ateliers.Ai.Assistant.Application.WebMonitoring;

/// <summary>既存 JSON の省略値0を RSS とする情報源種別。</summary>
public enum ContentSourceKind
{
    /// <summary>既存の公開 RSS / Atom。</summary>
    RssAtom = 0,
    /// <summary>公開 GitHub Releases API。</summary>
    GitHubReleases = 1
}
/// <summary>外部 API や UI に依存しない、設定済み情報源の取得境界。</summary>
public interface IContentSource
{
    /// <summary>設定の種別・オプションに従い正規化済み記事を返す。</summary>
    Task<IReadOnlyList<FeedArticle>> FetchAsync(SourceConfiguration source, CancellationToken token);
}
/// <summary>手動タグとは独立したリリースの取得メタデータ。</summary>
public sealed record GitHubReleaseMetadata(long ReleaseId, string Repository, string TagName, bool Draft, bool Prerelease);
/// <summary>本文や資格情報を含めず、GitHub 全体の再試行期限を伝える取得失敗。</summary>
public sealed class GitHubFetchException(string message, DateTimeOffset? retryAt = null) : Exception(message)
{
    /// <summary>null以外なら共有する GitHub API への通信を待機する。</summary>
    public DateTimeOffset? RetryAt { get; } = retryAt;
}
/// <summary>入力表記と取得元の保存キーを統一する。RSS の既存キー規則は変更しない。</summary>
public static partial class ContentSourceIdentity
{
    private const string ApiPrefix = "https://api.github.com/repos/";
    /// <summary>owner/repository またはアプリが保存した API URL を小文字の名前へ戻す。</summary>
    public static string Repository(string value)
    {
        var text = value.Trim();
        if (text.StartsWith(ApiPrefix, StringComparison.OrdinalIgnoreCase) && text.EndsWith("/releases", StringComparison.OrdinalIgnoreCase))
            text = text[ApiPrefix.Length..^9];
        if (text.Length > 140 || !RepositoryPattern().IsMatch(text) || text.Split('/').Any(part => part is "." or ".."))
            throw new WebMonitoringException("GitHub リポジトリは owner/repository の形式で指定してください（例: microsoft/vscode）。");
        return text.ToLowerInvariant();
    }
    /// <summary>取得元 URL を返す。異なる種別と RSS フィードを混同しない。</summary>
    public static string Normalize(ContentSourceKind kind, string value) => kind switch
    {
        ContentSourceKind.RssAtom => FeedIdentity.Source(value),
        ContentSourceKind.GitHubReleases => ApiPrefix + Repository(value) + "/releases",
        _ => throw new WebMonitoringException("情報源の種別が不正です。")
    };
    /// <summary>パス・クエリの注入を受け付けず、GitHub の名前に利用する ASCII を許可する。</summary>
    [GeneratedRegex(@"\A[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?/[A-Za-z0-9_.-]{1,100}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();
}
