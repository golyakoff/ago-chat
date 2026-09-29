using System.Net;

namespace Ago.Chat.Api.Billing;

/// <summary>
/// `26-286`: the IP allowlist half of ЮKassa's own documented webhook verification - notifications
/// originate only from ЮKassa's published notification networks, and a request from anywhere else is
/// rejected before it reaches the handler. Defense-in-depth, deliberately <b>not</b> the guarantee: the
/// real guarantee is the payment re-query in <c>ProcessYooKassaWebhookHandler</c> (a forger who reached
/// this endpoint from an allowlisted IP still cannot make ЮKassa's own API report a payment as
/// succeeded that is not). The allowlist raises the bar - an attacker must also spoof a source IP inside
/// ЮKassa's ranges past the gateway - but it depends on the real client IP being resolved correctly, so
/// it is hardening layered on top of the re-query, never a substitute for it.
///
/// <para>Lives in the host, not behind a port: it is pure computation over ЮKassa's own published CIDR
/// list and the framework's already-resolved <see cref="ConnectionInfo.RemoteIpAddress"/> - there is no
/// external resource to abstract. The IP arrives correct because `Program.cs`'s `UseForwardedHeaders`
/// (trusting the cluster's own Gateway network, `CompositionRoot`'s `ForwardedHeadersOptions`) has
/// already rewritten <see cref="ConnectionInfo.RemoteIpAddress"/> from the right-most trusted
/// `X-Forwarded-For` hop before any endpoint runs - the same resolved-client-IP every rate-limit bucket
/// in this host already trusts.</para>
/// </summary>
public static class YooKassaWebhookSourceGuard
{
    // ЮKassa's own published notification-source ranges (developers/using-api/webhooks), confirmed
    // 2026-09-29. Single addresses are expressed as /32 so one Contains check covers every entry.
    private static readonly IPNetwork[] AllowedNetworks =
    [
        IPNetwork.Parse("185.71.76.0/27"),
        IPNetwork.Parse("185.71.77.0/27"),
        IPNetwork.Parse("77.75.153.0/25"),
        IPNetwork.Parse("77.75.156.11/32"),
        IPNetwork.Parse("77.75.156.35/32"),
        IPNetwork.Parse("77.75.154.128/25"),
        IPNetwork.Parse("2a02:5180::/32"),
    ];

    /// <summary>True only if <paramref name="remoteIp"/> falls inside one of ЮKassa's published
    /// notification networks. A null address (no resolved client IP at all) is never allowed - the
    /// closed-by-default posture a security check must take.</summary>
    public static bool IsAllowed(IPAddress? remoteIp)
    {
        if (remoteIp is null)
        {
            return false;
        }

        // An IPv4 client can surface as an IPv4-mapped IPv6 address (::ffff:a.b.c.d) depending on the
        // socket; normalise so it matches the IPv4 networks above rather than silently failing every check.
        var candidate = remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4() : remoteIp;

        foreach (var network in AllowedNetworks)
        {
            if (network.Contains(candidate))
            {
                return true;
            }
        }

        return false;
    }
}
