using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetSiteBranding;

/// <summary>`25-160`: the console's "Почта @" screen's own read - an ordinary, uncached, per-operator
/// admin read, the identical shape `GetOfflineAutoReplyHandler`'s own remarks give for its sibling ("a
/// low-frequency admin read, not the per-message path").</summary>
public sealed record GetSiteBranding(SiteId SiteId, OperatorId RequestedBy);

/// <summary><see cref="LogoUrl"/> is <see langword="null"/> exactly when <see cref="Domain.Site.HasLogo"/>
/// is <see langword="false"/> - the console's own preview points an <c>&lt;img&gt;</c> at it directly
/// (this item's own Scope, point 7: "the same one item 2's client-side check already knows how to point
/// an <c>&lt;img&gt;</c> at once uploaded"), no second mechanism.</summary>
public sealed record SiteBrandingDto(
    string? BrandCompanyName, string? LogoUrl, LogoStatus LogoStatus, string? LogoRejectionReason);
