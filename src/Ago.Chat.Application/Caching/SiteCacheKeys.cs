using Ago.Chat.Domain;
using Ago.Platform.Abstractions;

namespace Ago.Chat.Application.Caching;

/// <summary>
/// The one place chat maps its own identities onto a cache key - the same role
/// `Ago.Chat.Application.Realtime.PrincipalKeys` plays for the connection registry's opaque
/// <see cref="PrincipalKey"/>. Two keys onto the same underlying row, not one: the widget handshake
/// path (`GetSiteConfigByPublicKeyHandler`) only ever has the public key at lookup time, while a hub
/// connection (`5-01`'s layer-2 origin check) only ever has the JWT's `site_id` claim - each caller
/// uses whichever key it actually has, never resolving one from the other just to share a cache entry.
/// </summary>
public static class SiteCacheKeys
{
    public static CacheKey ForPublicKey(string publicKey) => new($"site-config:{publicKey}");

    public static CacheKey ForSiteId(SiteId siteId) => new($"site-config:id:{siteId.Value}");
}
