using AngleSharp.Html.Parser;
using Ateliers.Ai.Assistant.Desktop.Presentation;

namespace Ateliers.Ai.Assistant.Desktop.Tests;

internal static class MarkdownTests
{
    public static Task Formatting()
    {
        var markdown = """
            # 見出し
            **太字** と *斜体*

            - 箇条書き
            - 二行目

            1. 番号付き
            2. 続き

            | 言語 | 値 |
            | --- | --- |
            | C# | 日本語 |

            ```csharp
            public string Message => "<script>はコードです</script>";
            ```
            """;
        var html = new MarkdownMessageRenderer().Render(markdown);
        var doc = new HtmlParser().ParseDocument(html);
        Assert.That(doc.QuerySelector("h1")?.TextContent == "見出し" && doc.QuerySelector("strong")?.TextContent == "太字", "見出し・太字");
        Assert.That(doc.QuerySelectorAll("ul li").Length == 2 && doc.QuerySelectorAll("ol li").Length == 2, "リスト");
        Assert.That(doc.QuerySelector("table tbody td")?.TextContent == "C#", "テーブル");
        Assert.That(doc.QuerySelector("pre code")?.ClassList.Contains("language-csharp") == true &&
            doc.QuerySelector("pre code")!.TextContent.Contains("<script>はコードです</script>"), "言語指定とコードの文字保持");
        Assert.That(doc.QuerySelector("script") is null, "コード本文からスクリプトを生成しない");
        return Task.CompletedTask;
    }
    public static Task Security()
    {
        var renderer = new MarkdownMessageRenderer();
        foreach (var markdown in new[]
        {
            "<script>window.pwned=true</script><img src=x onerror=alert(1)>",
            "[危険](javascript:alert%281%29) [file](file:///C:/secret) [data](data:text/html,evil)",
            "![追跡](https://example.com/tracker.png) [相対](/index.html) [shell](ms-settings:)",
            "<svg onload=alert(1)><a href='https://evil.example'>X</a></svg><iframe src='https://evil.example'></iframe>"
        })
        {
            var doc = new HtmlParser().ParseDocument(renderer.Render(markdown));
            Assert.That(doc.QuerySelector("script,img,iframe,svg,style,form") is null, "生 HTML・外部画像・埋め込みを除外");
            foreach (var anchor in doc.QuerySelectorAll("a[href]"))
                Assert.That(ExternalLinkPolicy.TryGetUri(anchor.GetAttribute("href"), out _), "リンクは許可された URL だけ");
            foreach (var element in doc.QuerySelectorAll("*"))
                Assert.That(element.Attributes.All(attribute => !attribute.Name.StartsWith("on") && attribute.Name != "style"), "イベント属性・任意スタイルを除外");
        }
        Assert.That(ExternalLinkPolicy.TryGetUri("https://example.com/page?q=日本語", out _) &&
            ExternalLinkPolicy.TryGetUri("http://example.com/", out _), "http(s) リンク");
        foreach (var blocked in new[] { "javascript:alert(1)", "file:///C:/a", "data:text/html,x", "https://chat-ui.invalid/index.html", "/relative", "https://user:pass@example.com/" })
            Assert.That(!ExternalLinkPolicy.TryGetUri(blocked, out _), "禁止 URL: " + blocked);
        Assert.That(TranscriptProtocol.IsAllowedResource(TranscriptProtocol.Document) &&
            TranscriptProtocol.IsAllowedResource("https://chat-ui.invalid/vendor/highlight.min.js"), "同梱資産を許可");
        foreach (var blocked in new[] { "https://example.com/chat.js", "https://chat-ui.invalid/secret.txt", "http://chat-ui.invalid/chat.js", "https://chat-ui.invalid/chat.js?x=1" })
            Assert.That(!TranscriptProtocol.IsAllowedResource(blocked), "外部・未知リソースを拒否");
        return Task.CompletedTask;
    }
    public static Task StreamingAndCache()
    {
        var cache = new TranscriptRenderCache();
        var partial = new MessageSnapshot("a", "```csharp\npublic class", "Assistant", "受信中", false, false);
        var first = cache.Render(new[] { partial });
        var final = cache.Render(new[] { partial with { Content = partial.Content + " Sample {}\n```", Detail = "中止", Final = true } });
        Assert.That(final[0].Html.Contains("Sample") && final[0].Final, "未完了フェンス・最終部分本文を再描画");
        var metadataOnly = cache.Render(new[] { partial with { Content = partial.Content + " Sample {}\n```", Detail = "失敗", Final = true } });
        Assert.That(ReferenceEquals(final[0].Html, metadataOnly[0].Html) && metadataOnly[0].Detail == "失敗", "本文キャッシュとメタデータの分離");
        var many = Enumerable.Range(0, 300).Select(index => partial with { Id = index.ToString(), Content = new string('あ', 500) }).ToArray();
        Assert.That(cache.Render(many).Length == 300 && cache.Render(Array.Empty<MessageSnapshot>()).Length == 0, "履歴増加とリセット");
        return Task.CompletedTask;
    }
    public static Task StaleUpdates()
    {
        var projection = new TranscriptProjection();
        var message = new RenderedMessage("a", "<p>原文</p>", "Assistant", "完了", false, true);
        var generation = projection.Generation;
        var first = projection.CreatePatch(generation, 1, new[] { message }, false)!;
        Assert.That(first.Messages.Length == 1 && first.Order!.Length == 1, "初回スナップショット");
        var second = projection.CreatePatch(generation, 2, new[] { message }, true)!;
        Assert.That(second.Messages.Length == 0 && second.Order is null && second.Dark, "既存本文の再送を避ける");
        Assert.That(projection.CreatePatch(generation, 1, new[] { message }, false) is null, "古い結果を拒否");
        projection.Reset();
        Assert.That(projection.CreatePatch(generation, 99, new[] { message }, false) is null, "旧会話の背景処理を拒否");
        var cleared = projection.CreatePatch(projection.Generation, 3, Array.Empty<RenderedMessage>(), false)!;
        Assert.That(cleared.Order!.Length == 0, "空会話を明示反映");
        return Task.CompletedTask;
    }
}
