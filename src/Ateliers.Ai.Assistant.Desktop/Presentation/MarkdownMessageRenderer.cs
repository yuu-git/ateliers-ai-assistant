using System;
using AngleSharp.Dom;
using Ganss.Xss;
using Markdig;

namespace Ateliers.Ai.Assistant.Desktop.Presentation;

/// <summary>汎用 Markdown 表示変換。原文を変更せず、DOM に挿入可能な安全化済み HTML を返す。</summary>
public sealed class MarkdownMessageRenderer
{
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder().UsePipeTables().DisableHtml().Build();
    private readonly HtmlSanitizer _sanitizer = new();
    private readonly object _sync = new();

    /// <summary>生 HTML を無効化した Markdown と、表示用 HTML の許可リストを構成する。</summary>
    public MarkdownMessageRenderer()
    {
        _sanitizer.AllowedTags.Clear();
        foreach (var tag in new[] { "p", "h1", "h2", "h3", "h4", "h5", "h6", "strong", "em", "ul", "ol", "li",
            "table", "thead", "tbody", "tr", "th", "td", "pre", "code", "blockquote", "br", "hr", "a" })
            _sanitizer.AllowedTags.Add(tag);
        _sanitizer.AllowedAttributes.Clear();
        foreach (var attribute in new[] { "href", "title", "class", "start" }) _sanitizer.AllowedAttributes.Add(attribute);
        _sanitizer.AllowedSchemes.Clear();
        _sanitizer.AllowedSchemes.Add("http");
        _sanitizer.AllowedSchemes.Add("https");
        _sanitizer.PostProcessNode += (sender, args) =>
        {
            if (args.Node is IElement element && element.TagName == "A" &&
                !ExternalLinkPolicy.TryGetUri(element.GetAttribute("href"), out _))
                element.RemoveAttribute("href");
        };
    }

    /// <summary>原文を変更せず、DOM の本文へ挿入できる安全化済み HTML に変換する。</summary>
    /// <param name="markdown">未変換の Markdown 原文。</param>
    /// <returns>安全化済みの表示用 HTML。</returns>
    public string Render(string markdown)
    {
        lock (_sync) return _sanitizer.Sanitize(Markdig.Markdown.ToHtml(markdown, _pipeline));
    }
}

/// <summary>表示から開ける外部リンクの URL 条件を定義する。</summary>
public static class ExternalLinkPolicy
{
    /// <summary>絶対 HTTP / HTTPS URL を検証し、内部ホストやユーザー情報付き URL を拒否する。</summary>
    /// <param name="value">ページから受け取った未検証の URL。</param>
    /// <param name="uri">検証に成功した絶対 URL。失敗時は null。</param>
    /// <returns>許可するリンク条件を満たす場合は true。</returns>
    public static bool TryGetUri(string? value, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            !parsed.IsWellFormedOriginalString() || (parsed.Scheme != "http" && parsed.Scheme != "https") ||
            parsed.Host.Length == 0 || parsed.UserInfo.Length != 0 || parsed.Host == TranscriptProtocol.Host)
            return false;
        uri = parsed;
        return true;
    }
}
