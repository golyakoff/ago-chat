namespace Ago.Chat.Contracts;

/// <summary>
/// `5-08`: `GET /api/v1/operators/me`'s response body - the console's own gap, found while building
/// this item: nothing before this let the console ask "what can I do" to decide whether to show the
/// admin nav item or the attachment-delete button. Raw permission value strings
/// (<c>"site:configure"</c>, ...), not an enum - the console has no reason to know the C#
/// <c>Permission</c> type, only to compare the strings it already receives against the ones it cares
/// about, the same "the wire carries values, not vocabulary" shape every other DTO here already uses.
///
/// `11-11`(console): <see cref="Locale"/> joins on the same terms - the active site's own locale, read
/// cache-aside through <c>GetSiteConfigByIdHandler</c> (the identical port `SendOfflineAutoReplyHandler`
/// already reuses for the same site), so the console has its answer at the same moment it has
/// <see cref="SiteId"/> rather than a second round trip - `11-10`'s own "no second network call"
/// principle, applied to the console's own bootstrap instead of the widget's. A raw string
/// (<c>Ago.Chat.Domain.Locale</c>'s own PascalCase member name, e.g. <c>"Ru"</c>), not the Domain
/// enum: <c>Ago.Chat.Contracts</c> has no project reference to <c>Ago.Chat.Domain</c> and must not
/// gain one for this - the same "the wire carries values, not vocabulary" shape <see cref="Permissions"/>
/// already uses on this exact record, and `AuthEndpoints.VisitorSessionResponse`'s own precedent for
/// this same enum on the widget's side of the wire.
/// </summary>
public sealed record OperatorPermissionsResponse(Guid OperatorId, Guid SiteId, IReadOnlyList<string> Permissions, string Locale);
