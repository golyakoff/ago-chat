namespace Ago.Chat.Contracts;

/// <summary>
/// `18-07`: one row of a channel-identified visitor's prior-conversation panel - the console's own
/// summary card, not a full transcript (opening one goes through the existing
/// `GetConversationHistoryAsOperator` read, the same "list is thin, opening reads the real thing"
/// split `ConversationSummaryDto`'s own remarks already establish for the admin's site-wide list).
///
/// <see cref="ClosedAt"/> is <see langword="null"/> both for a conversation still open and for one
/// closed before `Conversation.ClosedAt` existed - the wire cannot and need not tell those two apart;
/// <see cref="State"/> already says which is which for the first case.
///
/// <see cref="PreviewBody"/>/<see cref="PreviewAuthorKind"/>/<see cref="PreviewCreatedAt"/> come
/// together or not at all - a conversation that somehow has zero messages (created, never
/// written to) has none of the three, rather than a body with no author to attribute it to.
/// </summary>
public sealed record VisitorHistoryConversationDto(
    Guid ConversationId,
    string State,
    DateTimeOffset StartedAt,
    DateTimeOffset? ClosedAt,
    string? PreviewBody,
    string? PreviewAuthorKind,
    DateTimeOffset? PreviewCreatedAt);
