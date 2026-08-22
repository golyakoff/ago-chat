using Ago.Platform.Abstractions;

namespace Ago.Chat.Application.Caching;

/// <summary>
/// The one place chat maps its own identities onto a cache key - the same role
/// `Ago.Chat.Application.Realtime.PrincipalKeys` plays for the connection registry's opaque
/// <see cref="PrincipalKey"/>. Keyed by public key, not <c>SiteId</c>: the widget handshake path
/// (`GetSiteConfigByPublicKeyHandler`) only ever has the public key at lookup time - that is the whole
/// point of the lookup.
/// </summary>
public static class SiteCacheKeys
{
    public static CacheKey ForPublicKey(string publicKey) => new($"site-config:{publicKey}");
}
