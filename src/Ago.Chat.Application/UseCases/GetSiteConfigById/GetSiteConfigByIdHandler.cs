using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Caching;
using Ago.Chat.Application.UseCases.GetSiteByPublicKey;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;

namespace Ago.Chat.Application.UseCases.GetSiteConfigById;

/// <summary>
/// `5-01`, layer 2: the `SiteId`-keyed counterpart to <see cref="GetSiteConfigByPublicKeyHandler"/> -
/// same cache-aside + negative-caching shape, same underlying <c>sites</c> row, a different key because
/// a hub connection only ever has the JWT's `site_id` claim, never the public key. Reuses
/// <see cref="SiteConfigDto"/>/<see cref="SiteLookupResult"/> rather than declaring parallel types for
/// the identical shape.
/// </summary>
public sealed class GetSiteConfigByIdHandler(ISiteRepository sites, ICache cache)
{
    private static readonly CacheEntryOptions PositiveOptions = new(TimeSpan.FromMinutes(5));
    private static readonly CacheEntryOptions NegativeOptions = new(TimeSpan.FromSeconds(30));

    public async Task<SiteConfigDto?> HandleAsync(GetSiteConfigById query, CancellationToken cancellationToken)
    {
        var key = SiteCacheKeys.ForSiteId(query.SiteId);
        var result = await cache.GetOrCreateAsync(
            key, ct => LoadAsync(query.SiteId, key, ct), PositiveOptions, cancellationToken);
        return result.Found ? result.Config : null;
    }

    private async Task<SiteLookupResult> LoadAsync(SiteId siteId, CacheKey key, CancellationToken cancellationToken)
    {
        var site = await sites.GetByIdAsync(siteId, cancellationToken);
        if (site is null)
        {
            await cache.SetAsync(key, SiteLookupResult.NotFound, NegativeOptions, cancellationToken);
            return SiteLookupResult.NotFound;
        }

        return SiteLookupResult.Of(new SiteConfigDto(
            site.Id.Value, site.PublicKey, site.AllowedOrigins,
            site.WidgetConfig.PrimaryColorHex, site.WidgetConfig.Position, site.OfflineAutoReply));
    }
}
