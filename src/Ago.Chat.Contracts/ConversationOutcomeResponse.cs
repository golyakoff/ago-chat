namespace Ago.Chat.Contracts;

/// <summary>`18-10`: `GET /api/v1/conversations/{conversationId}/outcome`'s response body. The CLR
/// member name of `Ago.Chat.Domain.ConversationOutcome` - always one of <c>Unset</c>/<c>Converted</c>/
/// <c>NotConverted</c>/<c>FollowUpNeeded</c>, never absent (every conversation has one, defaulting to
/// <c>Unset</c>).</summary>
public sealed record ConversationOutcomeResponse(string Outcome);
