using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Caching;
using Ago.Platform.Abstractions;

namespace Ago.Chat.Application.UseCases.CheckCorsOrigin;

/// <summary>
/// `5-01`, layer 1 of the CORS design (see the backlog item's own remarks on why CORS cannot resolve
/// *which* site a preflight is for): "does any site allow this origin at all" - the same cache-aside +
/// negative-caching shape `GetSiteConfigByPublicKeyHandler` (`3-04`) already established, just keyed by
/// origin instead of public key. This does not, and must not, replace the per-site origin check a
/// caller makes once it has actually resolved which site a request is for - that is the real
/// tenant-isolation boundary, this is only what lets a legitimate preflight succeed at all.
/// </summary>
public sealed class CheckCorsOriginHandler(ISiteRepository sites, ICache cache)
{
    private static readonly CacheEntryOptions PositiveOptions = new(TimeSpan.FromMinutes(5));

    // Shorter than the positive TTL, same reasoning as GetSiteConfigByPublicKeyHandler's own negative
    // cache: an origin approved moments ago (a site just added to AllowedOrigins) should not read as
    // unknown for the full 5 minutes.
    private static readonly CacheEntryOptions NegativeOptions = new(TimeSpan.FromSeconds(30));

    public async Task<bool> HandleAsync(CheckOriginAllowed query, CancellationToken cancellationToken)
    {
        var key = CorsOriginCacheKeys.ForOrigin(query.Origin);
        var result = await cache.GetOrCreateAsync(key, ct => LoadAsync(query.Origin, key, ct), PositiveOptions, cancellationToken);
        return result.Allowed;
    }

    private async Task<OriginCheckResult> LoadAsync(string origin, CacheKey key, CancellationToken cancellationToken)
    {
        var allowed = await sites.AnyAllowsOriginAsync(origin, cancellationToken);
        if (!allowed)
        {
            // Written directly, with NegativeOptions - GetOrCreateAsync's own contract leaves a key
            // the factory already wrote alone, rather than overwriting it with the caller's longer
            // default TTL (GetSiteConfigByPublicKeyHandler's own comment explains the mechanism).
            await cache.SetAsync(key, OriginCheckResult.Denied, NegativeOptions, cancellationToken);
        }

        return allowed ? OriginCheckResult.Allow : OriginCheckResult.Denied;
    }

    // ICache now constrains `where T : class` (found live building this very handler - an
    // unconstrained generic T's T? return has no runtime effect for a value type, so caching a raw
    // bool could not distinguish a cold key from a genuinely-cached `false`, see
    // Ago.Platform.Abstractions.ICache's own remarks). A tiny reference-type wrapper, not a
    // Nullable<bool> return from LoadAsync itself - GetOrCreateAsync<T> still needs a concrete,
    // always-non-null T to cache the *false* outcome at all.
    private sealed record OriginCheckResult(bool Allowed)
    {
        public static readonly OriginCheckResult Allow = new(true);
        public static readonly OriginCheckResult Denied = new(false);
    }
}
