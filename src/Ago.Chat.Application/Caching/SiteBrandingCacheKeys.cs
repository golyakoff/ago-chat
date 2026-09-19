using Ago.Chat.Domain;
using Ago.Platform.Abstractions;

namespace Ago.Chat.Application.Caching;

/// <summary>`25-160`: the branding logo's own cache key - a sibling of <see cref="SiteCacheKeys"/>, kept
/// as its own class rather than a third method there because it names a genuinely different cached
/// shape (<see cref="SiteBrandingLogoPayload"/>, not <see cref="GetSiteByPublicKey.SiteConfigDto"/>)
/// with a different writer (the validating consumer's own write-through, `Ago.Chat.Worker`) and a
/// different reader (<c>Ago.Chat.Infrastructure.Email.EmailChannelAdapter</c>'s cache-aside GET) than
/// every key <see cref="SiteCacheKeys"/> already serves.</summary>
public static class SiteBrandingCacheKeys
{
    public static CacheKey ForLogo(SiteId siteId) => new($"site-branding-logo:{siteId.Value}");
}
