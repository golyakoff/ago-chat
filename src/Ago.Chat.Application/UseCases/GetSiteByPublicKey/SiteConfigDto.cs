using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetSiteByPublicKey;

/// <summary>What the widget handshake path actually needs from a <c>Site</c> - not the aggregate
/// itself, so the cached shape stays a plain, stable, serializable value (`caching.md`) rather than
/// coupling the cache entry's wire shape to `Ago.Chat.Domain.Site`'s own internals.
///
/// `11-01`: <see cref="WidgetPrimaryColorHex"/>/<see cref="WidgetPosition"/> are additive fields on
/// this existing cached DTO, not a second cached object next to it - the backlog item's own scope
/// note ("extend the cached DTO, not add a second cached object next to it"). Both
/// `GetSiteConfigByPublicKeyHandler` (this handler's own file) and `GetSiteConfigById.GetSiteConfigByIdHandler`
/// populate them from the same underlying `Site.WidgetConfig`, so a config write invalidates one real
/// row's worth of cached shape under two different keys (`SiteCacheKeys.ForPublicKey`/`ForSiteId`),
/// not two independently-drifting DTOs.</summary>
public sealed record SiteConfigDto(
    Guid SiteId, string PublicKey, IReadOnlyList<string> AllowedOrigins,
    string? WidgetPrimaryColorHex, Position WidgetPosition);
