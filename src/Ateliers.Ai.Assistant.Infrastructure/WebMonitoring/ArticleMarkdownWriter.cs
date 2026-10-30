using System.Text;
using Ateliers.Ai.Assistant.Application.Storage;
using Ateliers.Ai.Assistant.Application.WebMonitoring;

namespace Ateliers.Ai.Assistant.Infrastructure.WebMonitoring;

/// <summary>記事 ID だけからファイル名を生成し、Local の固定フォルダーに UTF-8 で原子的に出力する。</summary>
public sealed class ArticleMarkdownWriter(IApplicationPaths paths) : IArticleMarkdownWriter
{
    /// <summary>内部 ID に対応する相対パス。外部タイトルをパスに使用しない。</summary>
    public static string RelativePath(Guid id) => Path.Combine("web-monitoring", "articles", id.ToString("N") + ".md");
    /// <inheritdoc/>
    public async Task<string> WriteAsync(CollectedArticle article, CancellationToken token)
    {
        if (article.Id == Guid.Empty || string.IsNullOrWhiteSpace(article.Summary)) throw new StorageException("保存する要約がありません。");
        var relative = RelativePath(article.Id);
        var target = Path.Combine(paths.LocalDirectory, relative);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var content = $"# {OneLine(article.Article.Title)}\n\n" +
                $"- 元 URL: {OneLine(article.Article.Url ?? "なし")}\n- 公開日時: {article.Article.PublishedAt?.ToString("O") ?? "不明"}\n" +
                $"- 取得日時: {article.FetchedAt:O}\n- 使用モデル: {OneLine(article.Model ?? "不明")}\n" +
                $"- 情報源: {OneLine(article.Article.InputScope)}\n- {(article.Article.GitHubRelease is null ? "フィード" : "取得元")}: {OneLine(article.Article.Source)}\n\n## AI 要約\n\n{article.Summary}\n";
            if (article.Article.GitHubRelease is { } release)
                content += $"\n\n## リリースの取得情報\n\n- リポジトリ: {OneLine(release.Repository)}\n- Release ID: {release.ReleaseId}\n- リリースタグ: {OneLine(release.TagName)}\n- プレリリース: {release.Prerelease}\n";
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, target, true);
            return relative;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new StorageException("記事の Markdown を保存できませんでした。アクセス権・空き容量を確認してください。", exception); }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* 結果の成否を cleanup で上書きしない。 */ }
        }
    }
    /// <summary>外部メタデータが Markdown の構造やリンクを作らないよう、HTML を使わず文字をエスケープする。</summary>
    private static string OneLine(string text) => text.Replace("\r", " ").Replace("\n", " ")
        .Replace("\\", "\\\\").Replace("<", "\\<").Replace(">", "\\>").Replace("[", "\\[").Replace("]", "\\]")
        .Replace("*", "\\*").Replace("_", "\\_").Replace("`", "\\`").Replace("#", "\\#");
}
