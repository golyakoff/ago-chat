using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `18-11`: the read-side port behind the console's own "what are these conversations actually about"
/// report - hand-written SQL over the write model, never through an aggregate (`adr/0004`), the same
/// mechanism `IOperatorAnalyticsReadStore`/`IConversionReportReadStore` already use. A new port rather
/// than a sixth method on <see cref="IOperatorAnalyticsReadStore"/>: that store's every dimension
/// (channel, operator, referrer, campaign) attributes exactly one label per conversation, and its whole
/// `GROUPING SETS` shape depends on that - a tag is the opposite kind of dimension (zero, one, or many
/// per conversation), so folding it into that store would either misuse its shape or force every other
/// dimension in it to start worrying about fan-out too.
///
/// <para><b>Site-scoped, ordinary tenant isolation</b> - the same shape every other read store in this
/// file's neighbourhood already documents as "explicitly not `12-02`". This port's <c>WHERE</c> clause
/// cannot address another tenant's rows, and a <see cref="Domain.Tag"/> can only ever be attached to a
/// conversation on its own site in the first place (<see cref="Domain.Tag"/>'s own remarks).</para>
///
/// <para><b>A conversation with multiple tags counts once per tag it holds - stated here because it is
/// the one fact every caller of this port must not lose sight of.</b> <see cref="TagBreakdownBucket.ConversationCount"/>
/// summed across <see cref="TagBreakdownResult.ByTag"/> will not equal
/// <see cref="TagBreakdownResult.TotalConversationCount"/>, and that is correct, not a bug: tags are not
/// mutually exclusive categories the way channel/operator/outcome are, so a conversation tagged both
/// "Billing" and "Refund" is real evidence for both buckets, not evidence to be split or deduplicated
/// away between them.</para>
///
/// <para><b>The honesty check this whole item exists to keep visible.</b> <see cref="TagBreakdownResult.TaggedConversationCount"/>
/// and <see cref="TagBreakdownResult.PercentageTagged"/> answer "how much of this window does the
/// breakdown below actually cover" - a site whose operators tag inconsistently, or a site not yet
/// running `19-02`'s automatic categorization, will show a low percentage, and that has to render
/// alongside the breakdown, not be silently omitted the moment the number looks bad (the same discipline
/// <see cref="ConversionReportResult"/>'s own <c>UnsetCount</c> already holds itself to for a structurally
/// identical reason).</para>
///
/// <para><b>Why two queries over one connection, not one `GROUPING SETS` pass.</b> Every other read
/// store's own `GROUPING SETS` query works because joining in one more dimension never changes how many
/// rows one conversation contributes - channel, operator, referrer and campaign are each exactly one
/// label per conversation, so a single pass over `detail` can compute the site-wide total and every
/// dimension's per-bucket count without any row being counted twice. A tag join breaks that: joining
/// `conversation_tags` fans a conversation with two tags out to two rows, which is exactly the behaviour
/// <see cref="TagBreakdownResult.ByTag"/> needs and exactly the behaviour that would silently inflate
/// <see cref="TagBreakdownResult.TotalConversationCount"/>/<see cref="TagBreakdownResult.TaggedConversationCount"/>
/// if they were computed from the same joined-and-fanned-out row set. Rather than post-processing a
/// `GROUPING SETS` result back down to a distinct count in C#, this port's implementation runs the
/// distinct-count query and the per-tag fan-out query as two statements over one already-open connection
/// - two focused queries, each honestly shaped for the question it answers, instead of one query
/// contorted to answer two structurally different questions at once.</para>
///
/// <para><b>Conversion rate per tag, now that `18-10` has landed.</b> Each <see cref="TagBreakdownBucket"/>
/// carries the identical <c>Converted</c>/<c>NotConverted</c>/<c>RecordedCount</c>/<c>ConversionRate</c>
/// shape <see cref="ConversionBucket"/> already establishes, computed the same way: <c>FollowUpNeeded</c>
/// and <c>Unset</c> conversations are excluded from the rate's denominator entirely (this bucket does not
/// even carry those two counts - a tag breakdown is not the place to re-litigate `18-10`'s own outcome
/// vocabulary, only to slice its rate one more way).</para>
///
/// <para><b>Not a caching concern</b> (`CLAUDE.md` rule 8) - the identical reasoning every sibling read
/// store in this codebase already gives: pure observability for a human reading a report, at human
/// frequency, feeding no write or compare-and-set anywhere.</para>
/// </summary>
public interface ITagBreakdownReadStore
{
    /// <summary><paramref name="from"/> is inclusive, <paramref name="to"/> is exclusive - the same
    /// half-open convention every sibling read store in this codebase documents. Conversations are
    /// selected by <c>conversations.created_at</c> falling in that range; a tag applied after the window
    /// closes still counts, against the conversation it was applied to, in the window that conversation
    /// <em>started</em> - the same "the conversation's own start decides which window it belongs to"
    /// rule <see cref="IConversionReportReadStore.GetConversionReportAsync"/> already applies.</summary>
    Task<TagBreakdownResult> GetTagBreakdownAsync(
        SiteId siteId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
}
