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
/// `14-04`: <see cref="OfflineAutoReply"/> joins on exactly the same terms - an additive field on the
/// existing cached DTO, populated identically by both loaders, not a second cached object. The item
/// asks for the toggle to be read "the same cache-aside way `GetSiteConfigByPublicKeyHandler` already
/// reads every other per-site setting", and this is what that means concretely: the auto-reply script
/// rides the site-config entry, is evicted by the same `SiteSettingsChanged` invalidation, and costs
/// the per-message read no round trip of its own.
///
/// <para>It is <b>not</b> put on the wire by the handshake. <c>VisitorSessionResponse</c>
/// (`Ago.Chat.Api/Auth`) is built field by field from this DTO and lists what a visitor may see; a
/// tenant's scripted answers are not on that list and must not be, since the public key that reaches
/// this DTO is not a secret.</para>
public sealed record SiteConfigDto(
    Guid SiteId, string PublicKey, IReadOnlyList<string> AllowedOrigins,
    string? WidgetPrimaryColorHex, Position WidgetPosition, OfflineAutoReplySettings OfflineAutoReply);
