namespace Ago.Chat.Domain;

/// <summary>
/// `14-04`: <see cref="Site"/>'s second domain event, raised by
/// <see cref="Site.UpdateOfflineAutoReply"/>. Carries <see cref="PublicKey"/> alongside
/// <see cref="SiteId"/> for the identical reason <see cref="SiteWidgetConfigUpdated"/> does: both map
/// to the one <c>SiteSettingsChanged</c> integration event, whose consumer builds cache keys from
/// both values and would otherwise need a database round trip for one the publisher already held.
///
/// <para><b>Why a second event rather than reusing the first.</b> <see cref="SiteWidgetConfigUpdated"/>
/// names what changed, and an auto-reply script is not widget appearance - a consumer that one day
/// wants only appearance changes (a CDN purge, say) must be able to tell them apart, and renaming the
/// existing event to something generic would have made that distinction unrecoverable. They converge
/// at the integration-event boundary, not before it: both mappers produce <c>SiteSettingsChanged</c>,
/// because "this site's settings changed, drop its cache entries" genuinely is one fact.</para>
/// </summary>
public sealed record SiteOfflineAutoReplyUpdated(SiteId SiteId, string PublicKey, DateTimeOffset OccurredAt) : IDomainEvent;
