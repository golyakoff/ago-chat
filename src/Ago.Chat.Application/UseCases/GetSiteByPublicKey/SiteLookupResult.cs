namespace Ago.Chat.Application.UseCases.GetSiteByPublicKey;

/// <summary>
/// What actually gets cached - not <see cref="SiteConfigDto"/>? directly, because
/// <c>ICache.GetAsync</c>'s <see langword="null"/> already means "not cached at all"; a plain
/// nullable <see cref="SiteConfigDto"/> could not also mean "confirmed not found" (`caching.md`'s
/// negative-caching requirement) without conflating the two. This type is that third state, made
/// real rather than implied.
/// </summary>
internal sealed record SiteLookupResult(bool Found, SiteConfigDto? Config)
{
    public static readonly SiteLookupResult NotFound = new(false, null);

    public static SiteLookupResult Of(SiteConfigDto config) => new(true, config);
}
