namespace Ago.Chat.Contracts;

/// <summary>
/// `26-114`: `GET /api/v1/conversations/{conversationId}/visitor-summary`'s response body - the
/// contact-detail panel's own header facts, "Первый визит {date} · N диалог(ов)"
/// (`docs/design/26-111-thread-contact-detail-panel.md`'s decisions #4/#5).
///
/// <para><see cref="ConversationCount"/> counts this visitor's distinct conversations on this site,
/// <b>including</b> the one the operator currently has open - deliberately not the same number as
/// `/visitor-history`'s own list length, which excludes it (that endpoint's own remarks: "this visitor's
/// <em>other</em> conversations"). Both read the identical underlying scope - every non-blocked
/// conversation this visitor has on this site - so the two numbers always agree once the current
/// conversation is accounted for: <c>ConversationCount == historyList.Count + 1</c>.</para>
/// </summary>
public sealed record VisitorSummaryResponse(DateTimeOffset VisitorFirstSeenAt, int ConversationCount);
