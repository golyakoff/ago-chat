namespace Ago.Chat.Contracts;

/// <summary>
/// `18-01`: one search hit on the wire - the matched message plus just enough to recognise its
/// conversation before clicking through. Deliberately thin: opening a hit re-uses the existing
/// conversation-history read, positioned at <c>MessageId</c>, rather than this response carrying a
/// transcript of its own.
/// </summary>
public sealed record ConversationSearchResultDto(
    Guid ConversationId,
    Guid MessageId,
    int Sequence,
    string MatchedBody,
    string AuthorKind,
    DateTimeOffset CreatedAt,
    string ConversationState);
