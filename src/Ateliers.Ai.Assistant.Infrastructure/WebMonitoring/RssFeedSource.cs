using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Ateliers.Ai.Assistant.Application.WebMonitoring;

namespace Ateliers.Ai.Assistant.Infrastructure.WebMonitoring;

/// <summary>サイズを制限した RSS 2.0 / Atom 取得。本文ページや外部 XML エンティティは取得しない。</summary>
public sealed class RssFeedSource(HttpClient client) : IFeedSource
{
    /// <summary>展開後のフィード上限。巨大な XML の読み込みを防ぐ。</summary>
    public const int MaximumBytes = 2 * 1024 * 1024;
    /// <inheritdoc/>
    public async Task<IReadOnlyList<FeedArticle>> FetchAsync(string url, CancellationToken cancellationToken)
    {
        var original = FeedIdentity.Source(url);
        var uri = FeedIdentity.ValidateUrl(url);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            for (var redirect = 0; redirect <= 5; redirect++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("Ateliers-Ai-Assistant/0.3");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    if (response.Headers.Location is null || redirect == 5) throw new WebMonitoringException("フィードのリダイレクトが不正、または多すぎます。");
                    uri = FeedIdentity.ValidateUrl(new Uri(uri, response.Headers.Location).AbsoluteUri);
                    continue;
                }
                if (!response.IsSuccessStatusCode) throw new WebMonitoringException($"フィードを取得できませんでした（HTTP {(int)response.StatusCode}）。");
                if (response.Content.Headers.ContentLength > MaximumBytes) throw new WebMonitoringException("フィードが上限（2 MiB）を超えています。");
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var block = new byte[16384];
                int read;
                while ((read = await stream.ReadAsync(block, deadline.Token).ConfigureAwait(false)) != 0)
                {
                    if (buffer.Length + read > MaximumBytes) throw new WebMonitoringException("フィードが上限（2 MiB）を超えています。");
                    buffer.Write(block, 0, read);
                }
                buffer.Position = 0;
                return await Task.Run(() => Parse(buffer, original, uri, deadline.Token), deadline.Token).ConfigureAwait(false);
            }
            throw new WebMonitoringException("フィードを取得できませんでした。");
        }
        catch (XmlException exception) { throw new WebMonitoringException("RSS / Atom の XML が不正です。", exception); }
        catch (HttpRequestException exception) { throw new WebMonitoringException("フィードに接続できませんでした。URL と接続状態を確認してください。", exception); }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw new WebMonitoringException("フィードの取得がタイムアウトしました（30秒）。", exception); }
    }
    /// <summary>標準 XML API で2形式を読む。本文はテキスト資料として保持し、HTML として実行しない。</summary>
    private static IReadOnlyList<FeedArticle> Parse(Stream stream, string source, Uri baseUri, CancellationToken token)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null, MaxCharactersInDocument = MaximumBytes, MaxCharactersFromEntities = 0 });
        var document = XDocument.Load(reader);
        XNamespace atom = "http://www.w3.org/2005/Atom";
        var root = document.Root;
        var isAtom = root?.Name == atom + "feed";
        if (!isAtom && !(root?.Name == "rss" && root.Element("channel") is not null))
            throw new WebMonitoringException("RSS 2.0 または Atom フィードを指定してください。");
        var nodes = isAtom ? root!.Elements(atom + "entry") : root!.Element("channel")!.Elements("item");
        var articles = new List<FeedArticle>();
        foreach (var item in nodes)
        {
            token.ThrowIfCancellationRequested();
            if (articles.Count >= 200) throw new WebMonitoringException("フィードの記事数が上限（200件）を超えています。");
            var title = Limit((string?)(isAtom ? item.Element(atom + "title") : item.Element("title")) ?? "無題", 1000);
            var id = (string?)(isAtom ? item.Element(atom + "id") : item.Element("guid"));
            var link = isAtom ? (string?)item.Elements(atom + "link").FirstOrDefault(node =>
                (string?)node.Attribute("rel") is null or "alternate")?.Attribute("href") : (string?)item.Element("link");
            string? articleUrl = null;
            if (!string.IsNullOrWhiteSpace(link) && Uri.TryCreate(baseUri, link.Trim(), out var absolute))
            {
                try { articleUrl = FeedIdentity.NormalizeArticleUrl(absolute.AbsoluteUri); }
                catch (WebMonitoringException) { /* 不適切なリンクだけを除外し、記事本文は要約できる。 */ }
            }
            var rawDate = (string?)(isAtom ? item.Element(atom + "published") ?? item.Element(atom + "updated") : item.Element("pubDate"));
            DateTimeOffset? published = TryDate(rawDate);
            XNamespace contentNs = "http://purl.org/rss/1.0/modules/content/";
            var body = isAtom ? item.Element(atom + "content") : item.Element(contentNs + "encoded");
            var description = isAtom ? item.Element(atom + "summary") : item.Element("description");
            var text = (body ?? description)?.Value ?? "";
            var truncated = text.Length > 12000;
            var scope = (body is not null ? "フィード提供の本文（リンク先は未取得）" : "フィード提供の概要のみ（リンク先は未取得）") +
                (truncated ? "・先頭12,000文字" : "");
            id = string.IsNullOrWhiteSpace(id) ? null : Limit(id.Trim(), 4096);
            articles.Add(new FeedArticle(FeedIdentity.Key(source, id, articleUrl, title, published), source, id,
                articleUrl, title, published, Limit(text, 12000), scope));
        }
        return articles.DistinctBy(article => article.Key).ToArray();
    }
    /// <summary>RFC 822 の一般的な GMT 表記と ISO 8601 日時を UTC に正規化する。</summary>
    private static DateTimeOffset? TryDate(string? text)
    {
        if (text is null) return null;
        text = text.Trim();
        if (text.EndsWith(" GMT", StringComparison.OrdinalIgnoreCase)) text = text[..^4] + " +00:00";
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var date)
            ? date.ToUniversalTime() : null;
    }
    /// <summary>DB とプロンプトへ渡す外部文字列を制限する。</summary>
    private static string Limit(string text, int max) => text.Length <= max ? text : text[..max];

    /// <summary>リダイレクト・Cookie・Proxy を自動使用せず、解決済み公開 IP に接続する HTTP ハンドラー。</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = async (context, token) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
            if (addresses.Length == 0 || addresses.Any(ip => !FeedIdentity.IsPublicAddress(ip)))
                throw new HttpRequestException("Non-public feed address rejected.");
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (SocketException) { socket.Dispose(); }
                catch { socket.Dispose(); throw; }
            }
            throw new HttpRequestException("Feed connection failed.");
        }
    };
}
