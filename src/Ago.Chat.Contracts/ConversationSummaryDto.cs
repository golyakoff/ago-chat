namespace Ago.Chat.Contracts;

/// <summary>
/// `5-07`: the wire shape for one row of the console's queue view - deliberately thin (no message
/// body, no full history) since the queue view lists conversations, it does not read them; opening
/// one goes through the existing `JoinConversationAsync`/`GetHistoryAsync` hub methods, which already
/// answer "what did this conversation actually say."
/// </summary>
/// <summary>
/// `5-08`: <paramref name="OperatorId"/> is additive - <see langword="null"/> for any caller that
/// never populates it (`api-design.md`'s additive-only wire-contract rule, same as `MessageDto`'s own
/// `5-07` additions). The queue view's two lists never needed it (`Waiting` has none by definition,
/// `AssignedToMe` is always the caller's own id); the admin's site-wide list is the first caller that
/// does, since "who (if anyone) is handling this conversation" is the whole point of that view.
///
/// <para>`23-02`: <paramref name="OperatorName"/> is that operator's own display name, additive the
/// same way - <see langword="null"/> for the queue view (which never joins it in) and for a row that
/// predates the column. The console falls back to the id, never the other way round.</para>
/// </summary>
public sealed record ConversationSummaryDto(
    Guid ConversationId, Guid VisitorId, string State, DateTimeOffset CreatedAt, int OperatorUnreadCount,
    Guid? OperatorId = null, string? OperatorName = null);

/// <summary>
/// `GET /api/v1/conversations/queue`'s response body. Two lists rather than one filterable list: the
/// two halves answer genuinely different questions for an operator (`docs/vision.md`'s "no manual
/// claim" model - `Waiting` is read-only situational awareness, `AssignedToMe` is the operator's own
/// actionable work), and a console client always wants both together to render the queue view in one
/// round trip.
/// </summary>
public sealed record OperatorQueueResponse(
    IReadOnlyList<ConversationSummaryDto> Waiting, IReadOnlyList<ConversationSummaryDto> AssignedToMe);
