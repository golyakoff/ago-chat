namespace Ago.Chat.Contracts;

/// <summary>
/// `5-08`: `GET /api/v1/operators/me`'s response body - the console's own gap, found while building
/// this item: nothing before this let the console ask "what can I do" to decide whether to show the
/// admin nav item or the attachment-delete button. Raw permission value strings
/// (<c>"site:configure"</c>, ...), not an enum - the console has no reason to know the C#
/// <c>Permission</c> type, only to compare the strings it already receives against the ones it cares
/// about, the same "the wire carries values, not vocabulary" shape every other DTO here already uses.
/// </summary>
public sealed record OperatorPermissionsResponse(Guid OperatorId, Guid SiteId, IReadOnlyList<string> Permissions);
