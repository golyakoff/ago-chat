using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `18-10`: the read-side port behind the console's own conversion report - hand-written SQL over the
/// write model, never through the aggregate (`adr/0004`), the same mechanism `IOperatorAnalyticsReadStore`
/// already uses for `18-08`/`18-09`. A new port rather than a fourth/fifth method on that interface: the
/// question here ("what did operators say these conversations led to") is a genuinely different read
/// from "how fast/how often did we answer" - it reads one column (`conversations.outcome`) and needs
/// none of that store's message/channel joins, and bolting it onto an already-large interface would blur
/// two read models that happen to share a site and a date range but nothing about their query shape.
///
/// <para><b>Site-scoped, ordinary tenant isolation</b> - the same shape <see cref="IOperatorAnalyticsReadStore"/>
/// itself already documents as "explicitly not `12-02`" (the one cross-tenant read in this codebase).
/// This port's <c>WHERE</c> clause cannot address another tenant's rows.</para>
///
/// <para><b>Per-operator attribution, and why it needs none of `18-09`'s ambiguity.</b>
/// <see cref="IOperatorAnalyticsReadStore"/>'s own remarks spend several paragraphs on "who deserves
/// credit for answering", because a transfer can separate the operator who replied first from the one
/// currently holding a conversation. An outcome is not a credit-for-replying fact - it is what the
/// conversation as a whole led to, recorded by whichever operator did the recording, and the schema has
/// exactly one operator column on a conversation: <see cref="Conversation.OperatorId"/>, whoever is
/// (or was, at close) currently assigned. This query attributes every count to that column directly, no
/// tiebreak needed. A conversation with a recorded outcome that was never assigned to anyone is not a
/// real scenario this schema can produce (`SetConversationOutcomeHandler` does not require an assignment,
/// but every conversation that has received a message has one by the time an operator is in a position
/// to record anything about it) - the same "no data to attribute, name the gap rather than invent a
/// placeholder" precedent <see cref="IOperatorAnalyticsReadStore"/> already sets is still honoured: an
/// unassigned conversation's counts land in <see cref="ConversionReportResult.Overall"/> and nowhere in
/// <see cref="ConversionReportResult.ByOperator"/>.</para>
///
/// <para><b>Not a caching concern</b> (`CLAUDE.md` rule 8) - the identical reasoning
/// <see cref="IOperatorAnalyticsReadStore"/>'s own remarks give: pure observability for a human reading a
/// report, at human frequency, feeding no write or compare-and-set anywhere.</para>
/// </summary>
public interface IConversionReportReadStore
{
    /// <summary><paramref name="from"/> is inclusive, <paramref name="to"/> is exclusive - the same
    /// half-open convention <see cref="IOperatorAnalyticsReadStore.GetSiteAnalyticsAsync"/> documents.
    /// Conversations are selected by <c>conversations.created_at</c> falling in that range, exactly like
    /// that method - an outcome recorded after the window closes still counts, against the conversation
    /// it belongs to, in the window that conversation <em>started</em>.</summary>
    Task<ConversionReportResult> GetConversionReportAsync(
        SiteId siteId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
}
