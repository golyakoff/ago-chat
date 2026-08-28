namespace Ago.Chat.Contracts;

/// <summary>
/// `18-07`: `GET /api/v1/conversations/{conversationId}/visitor-history`'s response body.
///
/// <see cref="HasChannelIdentity"/> is the gate the backlog item's own Scope names as a hard
/// requirement - "a widget visitor has no such identity and the feature must not appear to have
/// anything to show for one". It is carried on every response, including the empty-but-gated one
/// (a widget visitor, or a channel visitor this is their first-ever conversation about), rather than
/// expressed as an HTTP status: the two "nothing to show" cases are semantically different
/// (structurally cannot exist, versus can exist and simply does not yet) and the console needs to
/// render them differently - no panel at all for the first, an empty-state panel for the second -
/// which a single "empty list" or a 404 could not distinguish without an extra round trip.
/// </summary>
public sealed record VisitorHistoryResponse(
    bool HasChannelIdentity,
    IReadOnlyList<VisitorHistoryConversationDto> Conversations,
    Guid? NextBeforeId);
