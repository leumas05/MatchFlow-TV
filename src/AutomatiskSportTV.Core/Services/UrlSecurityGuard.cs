using System.Net;
using AutomatiskSportTV.Core.Models;

namespace AutomatiskSportTV.Core.Services;

/// <summary>
/// Enforces HTTPS-only navigation, per-service trusted domain allowlists, SSRF protection,
/// and query-token redaction before displaying or logging URLs.
/// </summary>
public static class UrlSecurityGuard
{
    private static readonly Dictionary<StreamingServiceId, string[]> AllowedDomainSuffixes = new()
    {
        [StreamingServiceId.SvtPlay] = ["svtplay.se", "svt.se"],
        [StreamingServiceId.Tv4Play] = ["tv4play.se", "tv4.se", "bonniernews.se"],
        [StreamingServiceId.Viaplay] = ["viaplay.se", "viaplay.com", "viaplaygroup.com"],
        [StreamingServiceId.DisneyPlus] = ["disneyplus.com", "disney.com", "bamgrid.com"],
        [StreamingServiceId.PrimeVideo] = ["primevideo.com", "amazon.se", "amazon.com", "amazon.co.uk"],
        [StreamingServiceId.Netflix] = ["netflix.com", "nflxvideo.net"],
        [StreamingServiceId.Expressen] = ["expressen.se", "bonniernews.se", "staylive.tv", "staylive.io"],
        [StreamingServiceId.HboMax] = ["hbomax.com", "max.com", "eurosport.com", "wbd.com"]
    };

    /// <summary>
    /// Validates that a URL is HTTPS and does not target local/private network addresses (SSRF guard).
    /// </summary>
    public static bool IsSafeHttpsUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var host = uri.DnsSafeHost;
        if (string.IsNullOrWhiteSpace(host) ||
            host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IPAddress.TryParse(host, out var ip))
        {
            return !IsPrivateOrLoopback(ip);
        }

        return true;
    }

    /// <summary>
    /// Validates that a URL belongs to the trusted HTTPS domains for the given streaming service.
    /// </summary>
    public static bool IsTrustedServiceUrl(StreamingServiceId serviceId, string? url)
    {
        if (!IsSafeHttpsUrl(url) || !Uri.TryCreate(url!.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!AllowedDomainSuffixes.TryGetValue(serviceId, out var suffixes))
        {
            return false;
        }

        var host = uri.DnsSafeHost;
        return suffixes.Any(suffix =>
            host.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Validates navigation targets inside WebView2: allows about:blank, strict service hosts
    /// during automated scanning/playback, and general safe HTTPS (e.g. SSO/BankID) during interactive login.
    /// </summary>
    public static bool IsAllowedNavigation(StreamingServiceId serviceId, string? url, bool isInteractiveLogin)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            url.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return isInteractiveLogin
            ? IsSafeHttpsUrl(url)
            : IsTrustedServiceUrl(serviceId, url);
    }

    /// <summary>
    /// Strips query strings and fragments so tokens or session parameters are never logged or shown on screen.
    /// </summary>
    public static string SanitizeForDisplay(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        return $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";
    }

    private static bool IsPrivateOrLoopback(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal)
        {
            return true;
        }

        var bytes = ip.GetAddressBytes();
        if (bytes.Length == 4)
        {
            return bytes[0] == 10 ||
                   bytes[0] == 127 ||
                   (bytes[0] == 169 && bytes[1] == 254) ||
                   (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168);
        }

        return false;
    }
}
