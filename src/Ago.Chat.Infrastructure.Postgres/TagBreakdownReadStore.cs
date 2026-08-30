using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `18-11`: hand-written SQL over the write model, never through an aggregate (`adr/0004`), behind
/// <see cref="ITagBreakdownReadStore"/> (which carries the full "why this shape" statement, including the
/// once-per-tag counting rule and why this class runs two queries instead of one `GROUPING SETS` pass).
/// This class only turns that statement into SQL.
///
/// <para><b>Statement 1 - site-wide tagging coverage.</b> A `LEFT JOIN` from this window's conversations
/// out to `conversation_tags`, `count(distinct ...)` on both sides: `TotalConversationCount` never
/// double-counts a conversation with several tags because it counts `iw.id`, not the joined row; the same
/// is true of `TaggedConversationCount` counting `ct.conversation_id`, which is `NULL` (and so excluded
/// by `count`) for every conversation the join found nothing for. One join, two `count(distinct ...)`
/// expressions, no fan-out leaks into either number.</para>
///
/// <para><b>Statement 2 - the per-tag fan-out.</b> An `INNER JOIN` through `conversation_tags` to `tags`
/// - deliberately inner, not left: a conversation contributes a row here only once per tag it actually
/// holds, which is exactly <see cref="ITagBreakdownReadStore"/>'s own stated counting rule. `t.name`
/// joins in the tag's current display name (`Tag.Rename`'s own remarks: read fresh every time, not
/// cached), and the `filter (where ...)` pair mirrors `ConversionReportReadStore`'s own `Converted`/
/// `NotConverted` counting exactly, narrowed to this tag's own rows by the `group by t.id, t.name`.</para>
///
/// <para><b>Two round trips over one already-open connection, not two connections and not one combined
/// query.</b> See <see cref="ITagBreakdownReadStore"/>'s own class remarks for why a single `GROUPING
/// SETS` pass cannot serve both questions without either inflating the coverage counts or discarding the
/// per-tag fan-out. Both statements run over the same <see cref="NpgsqlConnection"/>, so this is one
/// database round trip's worth of connection overhead, not two - only the query execution itself happens
/// twice, and this report runs at human frequency (`ITagBreakdownReadStore`'s own "not a caching
/// concern" remarks), so that cost is not one this class needs to optimise away.</para>
/// </summary>
public sealed class TagBreakdownReadStore(NpgsqlDataSource dataSource) : ITagBreakdownReadStore
{
    // `18-10`'s own `nameof(...)` discipline, restated here rather than shared: a rename of
    // ConversationOutcome's members fails this class at compile time too, the same reason
    // ConversionReportReadStore keeps its own copies rather than reaching across files for one constant.
    private static readonly string ConvertedOutcome = nameof(ConversationOutcome.Converted);
    private static readonly string NotConvertedOutcome = nameof(ConversationOutcome.NotConverted);

    private const string OverallSql = """
        with in_window as (
            select c.id
            from conversations c
            where c.site_id = @SiteId
              and c.created_at >= @From
              and c.created_at < @To
        )
        select
            count(distinct iw.id) as "TotalConversationCount",
            count(distinct ct.conversation_id) as "TaggedConversationCount"
        from in_window iw
        left join conversation_tags ct on ct.conversation_id = iw.id
        """;

    private const string ByTagSql = """
        with in_window as (
            select c.id, c.outcome
            from conversations c
            where c.site_id = @SiteId
              and c.created_at >= @From
              and c.created_at < @To
        )
        select
            t.id as "TagId",
            t.name as "TagName",
            count(*) as "ConversationCount",
            count(*) filter (where iw.outcome = @ConvertedOutcome) as "ConvertedCount",
            count(*) filter (where iw.outcome = @NotConvertedOutcome) as "NotConvertedCount"
        from in_window iw
        inner join conversation_tags ct on ct.conversation_id = iw.id
        inner join tags t on t.id = ct.tag_id
        group by t.id, t.name
        """;

    public async Task<TagBreakdownResult> GetTagBreakdownAsync(
        SiteId siteId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var overall = await connection.QuerySingleAsync<TagBreakdownOverallRow>(new CommandDefinition(
            OverallSql,
            new { SiteId = siteId.Value, From = from, To = to },
            cancellationToken: cancellationToken));

        var rows = (await connection.QueryAsync<TagBreakdownRow>(new CommandDefinition(
            ByTagSql,
            new
            {
                SiteId = siteId.Value,
                From = from,
                To = to,
                ConvertedOutcome,
                NotConvertedOutcome,
            },
            cancellationToken: cancellationToken))).ToList();

        // Never zero itself - the same "nothing to compute a rate from yet" rule ConversionBucket's own
        // ConversionRate already applies, restated here for the site-wide coverage figure.
        double? percentageTagged = overall.TotalConversationCount == 0
            ? null
            : (double)overall.TaggedConversationCount / overall.TotalConversationCount;

        var byTag = rows
            .Select(r => new TagBreakdownBucket(
                new TagId(r.TagId),
                r.TagName,
                r.ConversationCount,
                r.ConvertedCount,
                r.NotConvertedCount,
                r.ConvertedCount + r.NotConvertedCount,
                BuildRate(r.ConvertedCount, r.NotConvertedCount)))
            .OrderBy(b => b.TagName, StringComparer.Ordinal)
            .ToList();

        return new TagBreakdownResult(
            overall.TotalConversationCount, overall.TaggedConversationCount, percentageTagged, byTag);
    }

    private static double? BuildRate(long converted, long notConverted)
    {
        var recorded = converted + notConverted;
        return recorded == 0 ? null : (double)converted / recorded;
    }
}
