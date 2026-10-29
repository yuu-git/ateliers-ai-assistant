using System.Security.Cryptography;
using System.Text;

namespace Ateliers.Ai.Assistant.Application.WebMonitoring;

/// <summary>再起動をまたぐフィードと記事の識別規則。未知のクエリや記事の更新日時は重複キーに加えない。</summary>
public static class FeedIdentity
{
    /// <summary>HTTP(S) URL を検証する。資格情報、ローカル名、プライベート IP は受け付けない。</summary>
    public static Uri ValidateUrl(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || uri.HostNameType == UriHostNameType.Unknown || uri.IsLoopback ||
            !uri.Host.Contains('.') && !System.Net.IPAddress.TryParse(uri.Host.Trim('[', ']'), out _) ||
            uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            System.Net.IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && !IsPublicAddress(ip))
            throw new WebMonitoringException("公開 HTTP / HTTPS フィードの URL を指定してください。ローカル接続・資格情報付き URL は利用できません。");
        return uri;
    }
    /// <summary>公開 IP だけを許可し、DNS が内部アドレスを返す場合も通信させない。</summary>
    public static bool IsPublicAddress(System.Net.IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (b.Length == 16) return (b[0] & 0xe0) == 0x20; // グローバルユニキャストのみ。
        return b[0] is not (0 or 10 or 127) && b[0] < 224 &&
            !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31) &&
            !(b[0] == 192 && b[1] == 168) && !(b[0] == 100 && b[1] is >= 64 and <= 127);
    }
    /// <summary>フィード URL のフラグメントだけを除去する。フィードを選ぶクエリは保持する。</summary>
    public static string Source(string url) => new UriBuilder(ValidateUrl(url)) { Fragment = "" }.Uri.AbsoluteUri;
    /// <summary>記事 URL から既知の追跡パラメーターだけを除去する。未知のパラメーターと順序は保持する。</summary>
    public static string NormalizeArticleUrl(string url)
    {
        var builder = new UriBuilder(ValidateUrl(url)) { Fragment = "" };
        builder.Query = string.Join("&", builder.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Where(part =>
        {
            var name = Uri.UnescapeDataString(part.Split('=')[0]);
            return !name.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("fbclid", StringComparison.OrdinalIgnoreCase) && !name.Equals("gclid", StringComparison.OrdinalIgnoreCase);
        }));
        return builder.Uri.AbsoluteUri;
    }
    /// <summary>GUID / Atom ID、URL、タイトルと公開日時の順で安定した SHA-256 キーを作る。</summary>
    public static string Key(string source, string? externalId, string? url, string title, DateTimeOffset? published) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source + "\n" +
            (!string.IsNullOrWhiteSpace(externalId) ? "id:" + externalId.Trim() : url is not null ? "url:" + NormalizeArticleUrl(url) :
            "fallback:" + title.Trim() + "\n" + published?.ToUniversalTime().ToString("O"))))).ToLowerInvariant();
}
