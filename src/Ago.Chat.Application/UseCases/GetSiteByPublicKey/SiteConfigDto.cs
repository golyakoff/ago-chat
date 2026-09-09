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
/// `23-11`: <see cref="ContactVisibility"/> joins on the identical terms <see cref="Tier"/> already
/// established - an additive field on the existing cached DTO, populated identically by both loaders,
/// and (like <see cref="Tier"/>) <b>never put on the wire by the widget handshake</b>: an anonymous
/// visitor holding the public key has no legitimate reason to learn whether this tenant masks its own
/// contact list. <c>ListVisitorContactDetailsHandler</c> is this field's one real reader - composing
/// through <c>GetSiteConfigByIdHandler</c> the same way `SendOfflineAutoReplyHandler`'s own remarks
/// describe ("composes rather than duplicates") - so the same event that evicts every other cached
/// setting (<c>SiteSettingsChanged</c>, via <c>SiteContactVisibilityUpdatedMapper</c>) keeps this field
/// fresh too, with no new cache-invalidation code.
/// `23-63`: <see cref="WidgetAttractAttention"/> joins on the identical terms <see cref="WidgetNoticeText"/>
/// did - an additive field on the existing cached DTO, populated identically by both loaders, and
/// <b>put on the wire</b> by the handshake: whether the launcher animates is a fact the widget's own
/// bootstrap needs to render correctly, the opposite of <see cref="OfflineAutoReply"/>/<see cref="Tier"/>'s
/// "never expose to the public key" reasoning.
/// `23-64`: <see cref="WidgetAutoOpenEnabled"/>/<see cref="WidgetAutoOpenDelaySeconds"/>/
/// <see cref="WidgetAutoOpenGreetingText"/> join on the identical terms <see cref="WidgetAttractAttention"/>
/// did - additive fields, populated identically by both loaders, <b>put on the wire</b> by the
/// handshake (the widget draws the greeting from exactly what this DTO carries, `adr/0148`).
/// <b>Also this DTO's one other real reader</b>: <c>MessageBatchWriter</c> reads
/// <see cref="WidgetAutoOpenEnabled"/>/<see cref="WidgetAutoOpenGreetingText"/> through this same
/// cache-aside DTO (via <c>GetSiteConfigByIdHandler</c>, already loaded once per batch group for
/// <see cref="Tier"/>'s own `RetentionClass.FromTier` stamp) to decide whether the visitor's first
/// real message should materialise the drawn greeting alongside it - the identical `adr/0031` "a
/// stamp, not a gate" carve-out from `CLAUDE.md` rule 8 that read already relies on: whether this
/// *particular* message gets an extra row beside it is not a decision anything's correctness depends
/// on, only what optional content accompanies an already-accepted write, so a cache entry up to five
/// minutes stale costs nothing a fresh read would not itself already risk (`adr/0148`'s own "the
/// tenant's configuration as it stands today" - today, at whichever moment this read actually runs).
/// `23-78`: <see cref="WidgetAllowAttachmentUploadsByDefault"/> joins on the identical terms
/// <see cref="Tier"/>/<see cref="ContactVisibility"/> already established - an additive field on the
/// existing cached DTO, populated identically by both loaders, and <b>never put on the wire by the
/// widget handshake</b> (the opposite of <see cref="WidgetAttractAttention"/>'s own "the widget's own
/// bootstrap needs to render correctly" reasoning): whether uploads start granted by default is not a
/// fact the widget renders, it is a fact <c>StartConversationHandler</c> reads once, server-side, to
/// seed a brand-new <see cref="Domain.Conversation"/>'s own grant at creation - the widget only ever
/// learns the *result* of that seeding, per conversation, through <c>VisitorJoinResult.HasAttachmentUploadGrant</c>
/// (`Ago.Chat.Contracts`), never this raw tenant-level setting. Exposing it to the public key an
/// anonymous visitor already holds would hand exactly the reconnaissance this backlog item's own
/// "says nothing an attacker could use" Done-when line forbids for the narrower per-conversation
/// refusal - a tenant's upload posture is no more the public key's business than
/// <see cref="ContactVisibility"/>'s own masking choice is.
public sealed record SiteConfigDto(
    Guid SiteId, string PublicKey, IReadOnlyList<string> AllowedOrigins,
    string? WidgetPrimaryColorHex, Position WidgetPosition, Locale WidgetLocale,
    OfflineAutoReplySettings OfflineAutoReply, string Tier,
    string? WidgetNoticeText, string? WidgetNoticeUrl, ContactVisibility ContactVisibility,
    bool WidgetAttractAttention, bool WidgetAutoOpenEnabled, AutoOpenDelay WidgetAutoOpenDelaySeconds,
    string? WidgetAutoOpenGreetingText, bool WidgetAllowAttachmentUploadsByDefault = false);
