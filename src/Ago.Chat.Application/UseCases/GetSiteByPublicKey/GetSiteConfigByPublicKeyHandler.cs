using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Caching;
using Ago.Platform.Abstractions;

namespace Ago.Chat.Application.UseCases.GetSiteByPublicKey;

/// <summary>
/// `3-04`'s first real cached read - the widget handshake path, `caching.md`'s "the hot one".
/// Cache-aside via <see cref="ICache.GetOrCreateAsync{T}"/>, which is also where the stampede
/// protection and TTL jitter this slice requires actually live (`Ago.Platform.Caching.Redis`) - this
/// handler only decides *what* to cache and for how long, never *how*.
/// </summary>
public sealed class GetSiteConfigByPublicKeyHandler(ISiteRepository sites, ICache cache)
{
    private static readonly CacheEntryOptions PositiveOptions = new(TimeSpan.FromMinutes(5));

    // caching.md: negative caching gets its own, shorter TTL than a real hit - short enough that a
    // site created moments ago is not stuck looking "not found" for the full 5 minutes.
    private static readonly CacheEntryOptions NegativeOptions = new(TimeSpan.FromSeconds(30));

    public async Task<SiteConfigDto?> HandleAsync(GetSiteConfigByPublicKey query, CancellationToken cancellationToken)
    {
        var key = SiteCacheKeys.ForPublicKey(query.PublicKey);
        var result = await cache.GetOrCreateAsync(
            key, ct => LoadAsync(query.PublicKey, key, ct), PositiveOptions, cancellationToken);
        return result.Found ? result.Config : null;
    }

    private async Task<SiteLookupResult> LoadAsync(string publicKey, CacheKey key, CancellationToken cancellationToken)
    {
        var site = await sites.GetByPublicKeyAsync(publicKey, cancellationToken);
        if (site is null)
        {
            // Written here, directly, with NegativeOptions - not left to GetOrCreateAsync's own
            // post-factory populate step, which would apply PositiveOptions instead. Its own doc
            // comment is the contract this relies on: a key the factory already wrote is left alone.
            await cache.SetAsync(key, SiteLookupResult.NotFound, NegativeOptions, cancellationToken);
            return SiteLookupResult.NotFound;
        }

        return SiteLookupResult.Of(new SiteConfigDto(
            site.Id.Value, site.PublicKey, site.AllowedOrigins,
            site.WidgetConfig.PrimaryColorHex, site.WidgetConfig.Position, site.Locale, site.OfflineAutoReply));
    }
}
