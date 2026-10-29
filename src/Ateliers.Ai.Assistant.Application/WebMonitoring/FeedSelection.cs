namespace Ateliers.Ai.Assistant.Application.WebMonitoring;

/// <summary>毎回の収集対象を公開日時の新しい10件に限定する。</summary>
public static class FeedSelection
{
    /// <summary>1回で登録・要約する記事数の上限。</summary>
    public const int MaximumArticles = 10;
    /// <summary>同じ記事を除外し、日時不明は既知日時の後でフィード内の順序を維持する。</summary>
    public static IReadOnlyList<FeedArticle> Latest(IReadOnlyList<FeedArticle> articles) => articles.DistinctBy(article => article.Key)
        .OrderByDescending(article => article.PublishedAt ?? DateTimeOffset.MinValue).Take(MaximumArticles).ToArray();
}
