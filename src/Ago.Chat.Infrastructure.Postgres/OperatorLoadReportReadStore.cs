using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `23-17`: <see cref="IOperatorLoadReportReadStore"/>'s own Postgres implementation - see that
/// interface for the full "why this shape" statement (held-versus-attributed, additional-computed-
/// never-stored, response time per load bucket). This class only turns that statement into SQL and
/// folds the result into <see cref="OperatorLoadBuckets"/>'s own configured buckets.
///
/// <para><b>Two queries, not one `GROUPING SETS` query.</b> A first version combined an operator's
/// totals and its per-exact-load breakdown into one query, the same
/// `GROUPING SETS ((operator_id), (operator_id, concurrent_load))` shape
/// <see cref="OperatorAnalyticsReadStore"/>'s own five-grouping-set query already uses successfully -
/// and, once a genuine reader-column-type bug in the row mapping was fixed (<c>concurrent_load</c> is
/// `count(*)`, always `bigint`, never the `int?` the row type first declared it as), that combined
/// query in fact computed every number correctly against the fixture below, `count(distinct
/// conversation_id)` included. It was kept split anyway: the totals row now comes from a plain
/// `GROUP BY operator_id` with no sibling grouping set to reason about, which is one fewer thing a
/// reviewer has to convince themselves a `DISTINCT` aggregate behaves correctly under, for the price of
/// one more round trip per report and the CTEs (<c>in_window</c>/<c>loaded</c>) repeated in both
/// statements - both acceptable at this report family's own human-frequency volume
/// (<see cref="IOperatorLoadReportReadStore"/>'s own remarks). Recorded here rather than silently
/// reverted, because the debugging path that led here is itself worth a future reader not repeating:
/// a wrong number first blamed on the query turned out to be the test's own hand-computed ground truth
/// missing that a *third*, unrelated, still-open conversation was part of the operator's load five
/// minutes later - see the scenario doc comment on <see cref="OperatorLoadReportReadStoreTests"/>
/// (`ago-chat`) for the corrected arithmetic.</para>
///
/// <para><b>Shape of each query.</b></para>
/// <list type="number">
/// <item><c>in_window</c> selects the site's own assignment intervals whose <c>started_at</c> falls in
/// the caller's half-open range.</item>
/// <item><c>loaded</c> joins each interval to its own operator (for <c>capacity</c> and
/// <c>display_name</c>) and computes <c>concurrent_load</c> with the identical correlated-subquery
/// shape <see cref="ConversationAssignmentOverlapQuery.CountHeldAtAsync"/> already proves against a
/// known fixture - <c>started_at &lt;= this interval's own started_at</c> and either still open or
/// ending strictly after it, which counts the interval itself. A concurrent load <em>equal to</em>
/// capacity is not additional - it fills the last open slot, it does not exceed it;
/// <c>concurrent_load &gt; operator_capacity</c> is the only condition this file ever tests for
/// "additional", nowhere stored as a flag.</item>
/// <item>The totals query groups <c>loaded</c> by <c>operator_id</c> alone: <c>IntervalCount</c> is
/// <c>count(*)</c>, <c>ConversationCount</c> is <c>count(distinct conversation_id)</c> (a conversation
/// transferred away and back to the same operator has two rows here and counts once),
/// <c>AdditionalCount</c> is <c>count(*) filter (where concurrent_load &gt; operator_capacity)</c>.
/// </item>
/// <item>The per-load query joins <c>loaded</c> to <c>replied</c> (the first message the same operator
/// sent, in the same conversation, inside that specific interval - never a different operator's reply,
/// never one outside this holding period) and groups by <c>(operator_id, concurrent_load)</c> - the
/// exact integer load, not a bucket; <see cref="GetOperatorLoadReportAsync"/> folds however many exact
/// values exist into <see cref="AnalyticsOptions.LoadBucketUpperBounds"/>'s own configured buckets in
/// C#, an exact fold (sum of sums, sum of counts, never an average-of-averages) - the backlog item's
/// own Scope: "the buckets are configuration, not literals in SQL."</item>
/// </list>
///
/// <para><b>Not a rate.</b> See <see cref="IOperatorLoadReportReadStore"/>'s own remarks: nothing here
/// is a fraction a thin sample could misrepresent, so <see cref="AnalyticsOptions.MinimumSampleForRate"/>
/// plays no part in this class, and results are returned in a stable order (operator id, then bucket
/// ascending) - a listing, never a ranking.</para>
/// </summary>
public sealed class OperatorLoadReportReadStore(NpgsqlDataSource dataSource, AnalyticsOptions analyticsOptions)
    : IOperatorLoadReportReadStore
{
    private static readonly string OperatorAuthorKind = nameof(MessageAuthorKind.Operator);

    private const string LoadedCte = """
        with in_window as (
            select ca.id, ca.conversation_id, ca.operator_id, ca.started_at, ca.ended_at
            from conversation_assignments ca
            where ca.site_id = @SiteId
              and ca.started_at >= @From
              and ca.started_at < @To
        ),
        loaded as (
            select
                iw.id,
                iw.conversation_id,
                iw.operator_id,
                iw.started_at,
                iw.ended_at,
                op.capacity as operator_capacity,
                op.display_name as operator_name,
                (
                    select count(*)
                    from conversation_assignments ca2
                    where ca2.operator_id = iw.operator_id
                      and ca2.started_at <= iw.started_at
                      and (ca2.ended_at is null or ca2.ended_at > iw.started_at)
                ) as concurrent_load
            from in_window iw
            join operators op on op.id = iw.operator_id and op.site_id = @SiteId
        )
        """;

    private const string TotalsSql = LoadedCte + """
        select
            operator_id as "OperatorId",
            max(operator_name) as "OperatorName",
            count(*) as "IntervalCount",
            count(distinct conversation_id) as "ConversationCount",
            count(*) filter (where concurrent_load > operator_capacity) as "AdditionalCount"
        from loaded
        group by operator_id
        """;

    private const string ByLoadSql = LoadedCte + """
        ,
        replied as (
            select
                l.id,
                (
                    select min(m.created_at)
                    from messages m
                    where m.conversation_id = l.conversation_id
                      and m.site_id = @SiteId
                      and m.author_kind = @OperatorAuthorKind
                      and m.author_id = l.operator_id
                      and m.created_at >= l.started_at
                      and (l.ended_at is null or m.created_at < l.ended_at)
                ) as first_reply_at
            from loaded l
        )
        select
            l.operator_id as "OperatorId",
            l.concurrent_load as "ConcurrentLoad",
            count(*) as "IntervalCount",
            count(r.first_reply_at) as "ReplyCount",
            coalesce(sum(extract(epoch from (r.first_reply_at - l.started_at))), 0)::double precision as "ReplySecondsSum"
        from loaded l
        join replied r on r.id = l.id
        group by l.operator_id, l.concurrent_load
        """;

    public async Task<IReadOnlyList<OperatorLoadSummary>> GetOperatorLoadReportAsync(
        SiteId siteId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var parameters = new { SiteId = siteId.Value, From = from, To = to, OperatorAuthorKind };

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var totalsTask = connection.QueryAsync<OperatorLoadTotalsRow>(
            new CommandDefinition(TotalsSql, parameters, cancellationToken: cancellationToken));
        var totals = (await totalsTask).ToList();

        var byLoadRows = (await connection.QueryAsync<OperatorLoadBucketRow>(
            new CommandDefinition(ByLoadSql, parameters, cancellationToken: cancellationToken))).ToList();

        var bounds = analyticsOptions.LoadBucketUpperBounds;
        var byLoadByOperator = byLoadRows.ToLookup(r => r.OperatorId);

        var summaries = totals.Select(row =>
        {
            // Fold however many exact concurrent-load values this operator's own rows carry into the
            // configured buckets - see this class's own remarks on why the fold happens here, in C#,
            // rather than in either query's own `GROUP BY`.
            var byBucketIndex = new SortedDictionary<int, (long IntervalCount, long ReplyCount, double ReplySecondsSum)>();
            foreach (var loadRow in byLoadByOperator[row.OperatorId])
            {
                var index = OperatorLoadBuckets.IndexOf(bounds, checked((int)loadRow.ConcurrentLoad));
                var existing = byBucketIndex.TryGetValue(index, out var value) ? value : default;
                byBucketIndex[index] = (
                    existing.IntervalCount + loadRow.IntervalCount,
                    existing.ReplyCount + loadRow.ReplyCount,
                    existing.ReplySecondsSum + loadRow.ReplySecondsSum);
            }

            var byLoad = byBucketIndex
                .Select(kvp => new OperatorLoadBucketEntry(
                    OperatorLoadBuckets.Label(bounds, kvp.Key),
                    kvp.Value.IntervalCount,
                    kvp.Value.ReplyCount,
                    kvp.Value.ReplyCount > 0 ? kvp.Value.ReplySecondsSum / kvp.Value.ReplyCount : null))
                .ToList();

            return new OperatorLoadSummary(
                new OperatorId(row.OperatorId),
                row.OperatorName,
                row.ConversationCount,
                row.IntervalCount,
                row.IntervalCount - row.AdditionalCount,
                row.AdditionalCount,
                byLoad);
        });

        return summaries.OrderBy(s => s.Operator.Value).ToList();
    }
}
