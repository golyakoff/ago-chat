using System.Net;
using System.Net.Sockets;

namespace Ago.Chat.Application.UseCases.RegisterWebhookEndpoint;

/// <summary>
/// `6-03`'s own scope: reject plain HTTP outright (an HMAC-signed payload over HTTP defeats its own
/// purpose, `adr/00XX`), and reject a URL that plainly targets a private/loopback/link-local address -
/// SSRF protection, since this system's own IP is what makes the outbound request later (`6-05`).
/// Pure and synchronous by design (no DNS resolution) so it stays unit-testable without a live network
/// probe (this item's own Done-when) - it catches every IP-literal case a malicious registration would
/// use directly (`https://127.0.0.1/...`, `https://169.254.169.254/...`) and the `localhost` name.
///
/// What this does <b>not</b> catch: a DNS hostname that resolves to a private address only at request
/// time. Resolving DNS here would make this "pure validator, unit-testable in isolation" and instead
/// require a live network call (or a fake resolver port) for a check whose result could change between
/// registration and every future delivery anyway (a TOCTOU gap no registration-time check can close).
/// The honest fix is at the other end of that gap: `6-05`'s dispatcher must re-validate the resolved
/// address immediately before it connects, not trust that this check already covered it. Flagging this
/// now so `6-05`'s planning session does not have to rediscover the reasoning.
///
/// `6-05`: that dispatcher is <see cref="Ago.Chat.Webhooks.HttpWebhookDeliveryClient"/>, and
/// <see cref="IsDisallowedResolvedAddress"/> below is what closes the gap - a
/// <c>SocketsHttpHandler.ConnectCallback</c> resolves DNS itself, checks every candidate address
/// against this exact predicate before ever opening a socket, and connects to the validated address
/// directly (never re-resolving the hostname a second time inside the actual connect, which would
/// reopen the same TOCTOU window this whole method exists to close). Exposed as a public method on
/// this same class rather than duplicated in the host project - one private-address definition, reused
/// by both the registration-time check and the delivery-time recheck, so the two can never drift apart
/// on what counts as "private."
/// </summary>
public static class WebhookUrlValidator
{
    private static readonly string[] DisallowedHostnames = ["localhost"];

    public static string? Validate(Uri url)
    {
        if (!url.IsAbsoluteUri || !string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return "Webhook endpoint URL must be an absolute https:// URL.";
        }

        if (TargetsDisallowedNetwork(url.Host, url.HostNameType))
        {
            return "Webhook endpoint URL must not target a private, loopback, or link-local address.";
        }

        return null;
    }

    private static bool TargetsDisallowedNetwork(string host, UriHostNameType hostNameType)
    {
        if (DisallowedHostnames.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return hostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
            && IPAddress.TryParse(host, out var address)
            && IsDisallowedResolvedAddress(address);
    }

    /// <summary>The resolved-address half of this validator's own SSRF check - public so
    /// `6-05`'s dispatcher can re-run it against whatever address DNS actually resolved to,
    /// immediately before connecting (this class's own remarks: the TOCTOU gap this file's
    /// registration-time check cannot close on its own).</summary>
    public static bool IsDisallowedResolvedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address)
            || address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || address.IsIPv6UniqueLocal)
        {
            return true;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var octets = address.GetAddressBytes();
        return octets[0] switch
        {
            0 => true, // 0.0.0.0/8 - "this network"
            10 => true, // 10.0.0.0/8
            127 => true, // 127.0.0.0/8 - loopback (also caught by IPAddress.IsLoopback above)
            169 when octets[1] == 254 => true, // 169.254.0.0/16 - link-local, includes 169.254.169.254
            172 when octets[1] is >= 16 and <= 31 => true, // 172.16.0.0/12
            192 when octets[1] == 168 => true, // 192.168.0.0/16
            _ => false,
        };
    }
}
