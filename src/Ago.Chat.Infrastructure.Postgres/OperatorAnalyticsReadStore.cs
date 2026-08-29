using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `18-08`: hand-written SQL over the write model, never through an aggregate (`adr/0004`) - the same
/// mechanism every other read model in this codebase uses, behind <see cref="IOperatorAnalyticsReadStore"/>
/// (which carries the full "why this shape" statement: the exact definitions of first response and
/// missed, and the channel-attribution tiebreak). This class only has to turn that statement into SQL.
///
/// <para><b>The literal enum-member strings are named constants, not string literals in the SQL</b> -
/// the same <c>nameof(AttachmentState.Deleted)</c> discipline <see cref="PlatformOverviewReadStore"/>
/// already applies, so a rename of <see cref="MessageAuthorKind"/> or <see cref="ConversationState"/>'s
/// members fails this class at compile time rather than leaving a query silently matching nothing.</para>
///
/// <para><b>Shape of the query, and why it is this shape:</b></para>
/// <list type="number">
/// <item><c>in_window</c> selects the site's conversations whose <c>created_at</c> falls in the
/// caller's half-open range first - the same "bound before you join" discipline
/// <see cref="PlatformOverviewReadStore"/>'s own remarks give for <c>messages</c>'s partitioning, applied
/// here to <c>conversations</c> (an unpartitioned, `site_id`-indexed table, so the bound's job here is
/// answering the caller's actual question - "this window" - not pruning partitions).</item>
/// <item>Two `LEFT JOIN LATERAL`s per conversation, not two separate aggregate CTEs joined back in:
/// <c>ch</c> resolves the visitor's earliest-linked <c>channel_identities</c> row (the tiebreak
/// <see cref="IOperatorAnalyticsReadStore"/>'s own remarks state and justify), and <c>ms</c> resolves
/// the conversation's first visitor and first operator message timestamps in one pass over `messages`
/// filtered by `conversation_id` - the same index (`IX_messages_conversation_id_sequence`) every other
/// per-conversation message read in this codebase already uses. `ON TRUE` is load-bearing: it is what
/// makes both joins outer or true LEFT JOINs behave like one - a conversation with no channel identity
/// or no messages yet still produces exactly one `detail` row, not zero.</item>
/// <item>The outer `GROUP BY GROUPING SETS ((), (channel_label))` computes the site-wide total and
/// every channel's bucket in one pass over `detail`, rather than one query for the total and a second,
/// separately-filtered one per channel - the same reason `PlatformOverviewReadStore` computes every
/// signal for a page of sites in one query instead of N+1. The total row comes back with
/// <c>channel_label = NULL</c> (Postgres's own grouping-set behaviour for a column outside the active
/// set), which <see cref="GetSiteAnalyticsAsync"/> uses to split the total from the per-channel rows -
/// <b>and this is also why a site with zero conversations in the window returns zero rows, not one
/// zeroed total row</b>: `GROUPING SETS` still groups actual input rows, and there is nothing to group
/// when `in_window` is empty. <see cref="GetSiteAnalyticsAsync"/> substitutes the honest zero bucket in
/// that case rather than letting an empty result read as a query failure.</item>
/// </list>
/// </summary>
public sealed class OperatorAnalyticsReadStore(NpgsqlDataSource dataSource) : IOperatorAnalyticsReadStore
{
    private static readonly string VisitorAuthorKind = nameof(MessageAuthorKind.Visitor);
    private static readonly string OperatorAuthorKind = nameof(MessageAuthorKind.Operator);
    private static readonly string ClosedState = nameof(ConversationState.Closed);

    /// <summary>The read-time label for a visitor with no <see cref="ChannelIdentity"/> row at all -
    /// every widget visitor, by <see cref="ChannelKind"/>'s own design (that type deliberately has no
    /// <c>Widget</c> member; see its remarks). Passed as a parameter rather than hardcoded into the SQL
    /// so the one literal exists once.</summary>
    private const string WidgetChannelLabel = "Widget";

    private const string SiteAnalyticsSql = """
        with in_window as (
            select c.id, c.site_id, c.visitor_id, c.state
            from conversations c
            where c.site_id = @SiteId
              and c.created_at >= @From
              and c.created_at < @To
        ),
        detail as (
            select
                iw.id,
                iw.state,
                coalesce(ch.kind, @WidgetLabel) as channel_label,
                ms.first_visitor_at,
                ms.first_operator_at
            from in_window iw
            left join lateral (
                select ci.kind
                from channel_identities ci
                where ci.site_id = iw.site_id and ci.visitor_id = iw.visitor_id
                order by ci.first_seen_at asc
                limit 1
            ) ch on true
            left join lateral (
                select
                    min(m.created_at) filter (where m.author_kind = @VisitorAuthorKind) as first_visitor_at,
                    min(m.created_at) filter (where m.author_kind = @OperatorAuthorKind) as first_operator_at
                from messages m
                where m.conversation_id = iw.id
            ) ms on true
        )
        select
            channel_label as "Channel",
            count(*) as "ConversationCount",
            count(*) filter (where state = @ClosedState and first_operator_at is null) as "MissedCount",
            (avg(extract(epoch from (first_operator_at - first_visitor_at)))
                filter (where first_operator_at is not null and first_visitor_at is not null))::double precision
                as "AverageFirstResponseSeconds"
        from detail
        group by grouping sets ((), (channel_label))
        """;

    public async Task<OperatorAnalyticsResult> GetSiteAnalyticsAsync(
        SiteId siteId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var rows = (await connection.QueryAsync<OperatorAnalyticsRow>(new CommandDefinition(
            SiteAnalyticsSql,
            new
            {
                SiteId = siteId.Value,
                From = from,
                To = to,
                WidgetLabel = WidgetChannelLabel,
                VisitorAuthorKind,
                OperatorAuthorKind,
                ClosedState,
            },
            cancellationToken: cancellationToken))).ToList();

        // No row at all means no conversation in the window (the class doc comment's own remarks on
        // why GROUPING SETS cannot produce a zeroed total from zero input rows) - the honest answer is
        // an explicit zero bucket, not an empty response the caller would have to special-case.
        var overallRow = rows.SingleOrDefault(r => r.Channel is null);
        var overall = overallRow is null
            ? new OperatorAnalyticsBucket(0, null, 0)
            : ToBucket(overallRow);

        var byChannel = rows
            .Where(r => r.Channel is not null)
            .Select(r => new OperatorAnalyticsChannelBucket(r.Channel!, ToBucket(r)))
            .OrderBy(c => c.Channel, StringComparer.Ordinal)
            .ToList();

        return new OperatorAnalyticsResult(overall, byChannel);
    }

    private static OperatorAnalyticsBucket ToBucket(OperatorAnalyticsRow row) =>
        new(row.ConversationCount, row.AverageFirstResponseSeconds, row.MissedCount);
}
