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
/// `11-10`: <see cref="WidgetLocale"/> joins on exactly the same terms `WidgetPrimaryColorHex`/
/// `WidgetPosition` did in `11-01` - an additive field on the existing cached DTO, populated
/// identically by both loaders, not a second cached object. Unlike <see cref="OfflineAutoReply"/>,
/// this one <b>is</b> put on the wire by the handshake (<c>AuthEndpoints.VisitorSessionResponse</c>):
/// it is a public setting a tenant chose for their own widget, not a scripted answer the public key
/// (not a secret) should never expose - the same distinction `SiteConfigDto`'s own remarks already
/// draw between the two.
/// `13-06`: <see cref="Tier"/> joins on the same "additive field on the existing cached DTO" terms -
/// the message-write path (`MessageBatchWriter`) reads it to stamp `Message.RetentionClass`
/// (`RetentionClass.FromTier`) without a per-message billing query, `adr/0031`'s own carve-out from
/// `CLAUDE.md` rule 8 ("a stamp, not a gate" - nothing about whether a write may proceed depends on
/// this value, only what gets recorded once it already has). Not put on the wire by the widget
/// handshake, the same reasoning <see cref="OfflineAutoReply"/> already states: a tenant's billing tier
/// is not something an anonymous visitor holding the public key should be able to read.
/// `16-04`: <see cref="WidgetNoticeText"/>/<see cref="WidgetNoticeUrl"/> join on exactly the same terms
/// <see cref="WidgetLocale"/> did - additive fields on the existing cached DTO, populated identically by
/// both loaders, and <b>put on the wire</b> by the handshake: this is the visitor-facing point of the
/// whole item (a visitor must see the notice before typing anything), the opposite of
/// <see cref="OfflineAutoReply"/>/<see cref="Tier"/>'s "never expose to the public key" reasoning.
public sealed record SiteConfigDto(
    Guid SiteId, string PublicKey, IReadOnlyList<string> AllowedOrigins,
    string? WidgetPrimaryColorHex, Position WidgetPosition, Locale WidgetLocale,
    OfflineAutoReplySettings OfflineAutoReply, string Tier,
    string? WidgetNoticeText, string? WidgetNoticeUrl);
