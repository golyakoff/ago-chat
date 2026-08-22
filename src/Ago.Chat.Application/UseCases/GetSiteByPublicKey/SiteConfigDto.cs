namespace Ago.Chat.Application.UseCases.GetSiteByPublicKey;

/// <summary>What the widget handshake path actually needs from a <c>Site</c> - not the aggregate
/// itself, so the cached shape stays a plain, stable, serializable value (`caching.md`) rather than
/// coupling the cache entry's wire shape to `Ago.Chat.Domain.Site`'s own internals.</summary>
public sealed record SiteConfigDto(Guid SiteId, string PublicKey, IReadOnlyList<string> AllowedOrigins);
