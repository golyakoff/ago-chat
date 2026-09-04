using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `18-10`: hand-written SQL over the write model, never through the aggregate (`adr/0004`), behind
/// <see cref="IConversionReportReadStore"/> (which carries the full "why this shape" statement,
/// including the attribution reasoning and the not-a-verified-sale framing). This class only turns that
/// statement into SQL.
///
/// <para><b>Much simpler than <see cref="OperatorAnalyticsReadStore"/>, deliberately.</b> That query
/// needs two `LEFT JOIN LATERAL`s over `messages`/`channel_identities` to answer "how fast" and "which
/// channel." This one reads a single column already sitting on `conversations` - no join, no per-row
/// correlated lookup - because an outcome is something an operator records directly on the conversation,
/// not something derived from its message history.</para>
///
/// <para><b>One `GROUPING SETS` query, two grouping sets, not four.</b>
/// <c>GROUP BY GROUPING SETS ((outcome), (operator_id, outcome))</c> computes the site-wide count per
/// outcome and the per-operator count per outcome in one pass over `in_window` - the identical "one pass
/// instead of two separately-filtered queries" reasoning <see cref="OperatorAnalyticsReadStore"/>'s own
/// remarks give for its own three-grouping-set query, narrowed to two here because there is no
/// per-channel dimension for this report (an outcome has no channel-shaped question the way response
/// time and miss rate do). <c>grouping(operator_id)</c> disambiguates the same way that class's own
/// <c>grouping(attributed_operator_id)</c> does: the site-wide grouping set's rows carry a structural
/// <c>NULL</c> in <c>operator_id</c> regardless of the data, and the per-operator grouping set's own
/// "recorded on a conversation nobody was ever assigned to" rows carry a real one - <c>grouping() == 1</c>
/// picks out the former, <c>grouping() == 0</c> the latter.</para>
///
/// <para><b>No empty-result special case, unlike <see cref="OperatorAnalyticsReadStore"/>.</b> That
/// class must substitute an explicit zero bucket when a site has zero conversations in the window,
/// because `GROUPING SETS` over zero input rows produces zero output rows. This class needs no such
/// substitution: <see cref="BuildBucket"/> already returns an honest all-zero, null-rate bucket when
/// handed an empty row sequence, so "no rows came back" and "the query ran over nothing" collapse into
/// the same code path rather than needing a second one.</para>
/// </summary>
public sealed class ConversionReportReadStore(NpgsqlDataSource dataSource, AnalyticsOptions analyticsOptions)
    : IConversionReportReadStore
{
    // `18-08`'s own `nameof(...)` discipline: a rename of ConversationOutcome's members fails this
    // class at compile time rather than leaving the SQL's `GROUP BY` silently unmatched by C# code
    // that still says the old name.
    private static readonly string ConvertedOutcome = nameof(ConversationOutcome.Converted);
    private static readonly string NotConvertedOutcome = nameof(ConversationOutcome.NotConverted);
    private static readonly string FollowUpNeededOutcome = nameof(ConversationOutcome.FollowUpNeeded);
    private static readonly string UnsetOutcome = nameof(ConversationOutcome.Unset);

    private const string ConversionReportSql = """
        with in_window as (
            select c.operator_id, c.outcome
            from conversations c
            where c.site_id = @SiteId
              and c.created_at >= @From
              and c.created_at < @To
        )
        select
            operator_id as "OperatorId",
            outcome as "Outcome",
            count(*) as "Count",
            grouping(operator_id) as "OperatorGrouping"
        from in_window
        group by grouping sets ((outcome), (operator_id, outcome))
        """;

    public async Task<ConversionReportResult> GetConversionReportAsync(
        SiteId siteId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var rows = (await connection.QueryAsync<ConversionReportRow>(new CommandDefinition(
            ConversionReportSql,
            new { SiteId = siteId.Value, From = from, To = to },
            cancellationToken: cancellationToken))).ToList();

        var overall = BuildBucket(rows.Where(r => r.OperatorGrouping == 1));

        // `OperatorGrouping == 0` selects the per-operator grouping set's own rows; `OperatorId is not
        // null` then drops that set's "never assigned to anyone" bucket - there is no operator to
        // report it under, the same exclusion `IOperatorAnalyticsReadStore`'s own remarks state for the
        // identical shape.
        //
        // `23-16`: this is a genuine *ranking* (a reader compares operators against each other on this
        // number), so it is where `AnalyticsOptions.MinimumSampleForRate` actually bites - see that
        // class's own remarks. Operators whose own `RecordedCount` meets the threshold sort first,
        // ranked by their own `ConversionRate` descending; everyone else follows, ranked by
        // `RecordedCount` descending instead - never by a rate built on too few conversations, even
        // though that rate still renders, in full, next to its own fraction. `Operator.Value` is the
        // final tie-break, both groups, so the order is fully deterministic rather than merely "usually
        // stable" - Postgres's own row order for equal keys is not guaranteed.
        var byOperator = rows
            .Where(r => r.OperatorGrouping == 0 && r.OperatorId is not null)
            .GroupBy(r => r.OperatorId!.Value)
            .Select(g => new ConversionOperatorBucket(new OperatorId(g.Key), BuildBucket(g)))
            .OrderByDescending(o => MeetsSampleThreshold(o.Bucket))
            .ThenByDescending(o => MeetsSampleThreshold(o.Bucket) ? o.Bucket.ConversionRate : null)
            .ThenByDescending(o => o.Bucket.RecordedCount)
            .ThenBy(o => o.Operator.Value)
            .ToList();

        return new ConversionReportResult(overall, byOperator);
    }

    private bool MeetsSampleThreshold(ConversionBucket bucket) =>
        bucket.RecordedCount >= analyticsOptions.MinimumSampleForRate;

    private static ConversionBucket BuildBucket(IEnumerable<ConversionReportRow> rows)
    {
        long converted = 0, notConverted = 0, followUpNeeded = 0, unset = 0;
        foreach (var row in rows)
        {
            if (row.Outcome == ConvertedOutcome)
            {
                converted = row.Count;
            }
            else if (row.Outcome == NotConvertedOutcome)
            {
                notConverted = row.Count;
            }
            else if (row.Outcome == FollowUpNeededOutcome)
            {
                followUpNeeded = row.Count;
            }
            else if (row.Outcome == UnsetOutcome)
            {
                unset = row.Count;
            }
        }

        // The backlog item's own load-bearing decision, restated in code: Unset conversations (nobody
        // has recorded an outcome) and FollowUpNeeded ones (recorded, but not yet resolved either way)
        // are both excluded from this fraction entirely - conflating "not yet answered" with "answered
        // no" would misrepresent what operators actually said.
        var recorded = converted + notConverted;
        double? rate = recorded == 0 ? null : (double)converted / recorded;

        return new ConversionBucket(converted, notConverted, followUpNeeded, unset, recorded, rate);
    }
}
