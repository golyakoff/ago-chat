namespace Ago.Chat.Domain;

/// <summary>
/// `25-160`: raised by <see cref="Site.UpdateBrandCompanyName"/>. Maps to the same
/// <c>SiteSettingsChanged</c> integration event every other <see cref="Site"/> settings write already
/// converges on (`Site.UpdateWidgetConfig`'s own remarks) - "written through the ordinary site-settings
/// path" is this backlog item's own words for it. Nothing in the cached <c>SiteConfigDto</c> actually
/// carries this field today (only <c>Ago.Chat.Infrastructure.Email.EmailChannelAdapter</c> reads it, off
/// a freshly loaded <see cref="Site"/>, never the cache), but raising the identical event this
/// aggregate's every other write already raises keeps one mechanism for "this site's settings changed"
/// rather than a second, silent one for this field alone - the same "one options group, one field" spirit
/// <see cref="SiteContactVisibilityUpdated"/> already accepted for a field the wire response never
/// carries either.
/// </summary>
public sealed record SiteBrandCompanyNameUpdated(SiteId SiteId, string PublicKey, DateTimeOffset OccurredAt) : IDomainEvent;
