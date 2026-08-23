namespace Ago.Chat.Contracts;

/// <summary>
/// `5-07`: the wire shape for one row of the console's queue view - deliberately thin (no message
/// body, no full history) since the queue view lists conversations, it does not read them; opening
/// one goes through the existing `JoinConversationAsync`/`GetHistoryAsync` hub methods, which already
/// answer "what did this conversation actually say."
/// </summary>
public sealed record ConversationSummaryDto(
    Guid ConversationId, Guid VisitorId, string State, DateTimeOffset CreatedAt, int OperatorUnreadCount);

/// <summary>
/// `GET /api/v1/conversations/queue`'s response body. Two lists rather than one filterable list: the
/// two halves answer genuinely different questions for an operator (`docs/vision.md`'s "no manual
/// claim" model - `Waiting` is read-only situational awareness, `AssignedToMe` is the operator's own
/// actionable work), and a console client always wants both together to render the queue view in one
/// round trip.
/// </summary>
public sealed record OperatorQueueResponse(
    IReadOnlyList<ConversationSummaryDto> Waiting, IReadOnlyList<ConversationSummaryDto> AssignedToMe);
