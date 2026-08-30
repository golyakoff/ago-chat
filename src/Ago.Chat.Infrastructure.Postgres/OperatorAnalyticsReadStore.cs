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
/// answering the caller's actual question - "this window" - not pruning partitions). `18-09` adds
/// <c>c.operator_id</c> to this CTE's own projection - the conversation's <em>currently assigned</em>
/// operator, needed only as the missed-conversation fallback the <c>detail</c> CTE computes below.</item>
/// <item>Two `LEFT JOIN LATERAL`s per conversation, not two separate aggregate CTEs joined back in:
/// <c>ch</c> resolves the visitor's earliest-linked <c>channel_identities</c> row (the tiebreak
/// <see cref="IOperatorAnalyticsReadStore"/>'s own remarks state and justify), and <c>ms</c> resolves
/// the conversation's first visitor and first operator message timestamps in one pass over `messages`
/// filtered by `conversation_id` - the same index (`IX_messages_conversation_id_sequence`) every other
/// per-conversation message read in this codebase already uses. `ON TRUE` is load-bearing: it is what
/// makes both joins outer or true LEFT JOINs behave like one - a conversation with no channel identity
/// or no messages yet still produces exactly one `detail` row, not zero. `18-09` adds one more column to
/// <c>ms</c>: <c>first_operator_id</c>, the <c>author_id</c> of the earliest operator-authored message,
/// pulled from the same single pass over `messages` via <c>array_agg(... order by created_at)
/// filter (...)  [1]</c> rather than a second correlated subquery re-reading the same rows.</item>
/// <item><c>ch</c>'s own <c>where</c> now also requires <c>ci.active</c> (`14-12`) - an unlinked
/// <see cref="ChannelIdentity"/> must not keep winning this tiebreak once
/// <see cref="ChannelIdentity.Unlink"/> has run, the same "excluded from routing/preference/lookup"
/// (`adr/0079` decision 4) the write side already enforces in
/// <see cref="Application.Abstractions.IChannelIdentityRepository.FindMostRecentForVisitorAsync"/>. This
/// item deliberately does not also reconcile this tiebreak's own "earliest identity" rule with that
/// method's "most recent" rule - `adr/0079`'s own remarks name that a real, separate follow-up once a
/// preferred channel can exist, not something bundled into `14-12`.</item>
/// <item><c>attributed_operator_id</c>, `18-09`'s own addition and the answer to the backlog item's
/// stated ambiguity: <c>coalesce(first_operator_id, assigned_operator_id)</c>. See
/// <see cref="IOperatorAnalyticsReadStore"/>'s remarks for the full reasoning; in one line, a conversation
/// that got a reply attributes to whoever gave it, even after a `18-02` transfer moves
/// <c>assigned_operator_id</c> elsewhere, and only a conversation nobody ever answered falls back to
/// whoever was holding it when it closed.</item>
/// <item>The outer `GROUP BY GROUPING SETS ((), (channel_label), (attributed_operator_id))` computes the
/// site-wide total, every channel's bucket, and every operator's bucket in one pass over `detail`,
/// rather than three separately-filtered queries - the same reason `PlatformOverviewReadStore` computes
/// every signal for a page of sites in one query instead of N+1. The total row comes back with both
/// <c>channel_label</c> and <c>attributed_operator_id</c> as `NULL` (Postgres's own grouping-set
/// behaviour for a column outside the active set) - <b>and so does the per-operator grouping set's own
/// "nobody was ever assigned" bucket</b>, a real `NULL` this time, not a structural one, so the two are
/// genuinely indistinguishable by column value alone. `grouping(attributed_operator_id)` (`1` when the
/// column is outside the active set, `0` when it is genuinely being grouped on, `NULL` value or not) is
/// the disambiguator <see cref="GetSiteAnalyticsAsync"/> reads to tell them apart - the total row has
/// `grouping = 1`; the "never assigned to anyone" bucket has `grouping = 0` and is dropped, the same
/// "nothing to attribute this to" gap this class already names for a widget visitor's channel, except
/// here there is no honest label to fall back to, so the row is excluded from
/// <see cref="OperatorAnalyticsResult.ByOperator"/> entirely rather than reported under a manufactured
/// placeholder operator. <b>This is also why a site with zero conversations in the window returns zero
/// rows, not one zeroed total row</b>: `GROUPING SETS` still groups actual input rows, and there is
/// nothing to group when `in_window` is empty. <see cref="GetSiteAnalyticsAsync"/> substitutes the
/// honest zero bucket in that case rather than letting an empty result read as a query failure.</item>
/// <item>`18-13`: <c>AverageDurationSeconds</c> is one more `avg(...)` on the same pass over
/// <c>detail</c>, not a second query - `created_at`/`closed_at` ride along from `in_window` through
/// `detail` for exactly this, the same "add the raw columns the new aggregate needs to the CTE that
/// already selects the row" shape the first-response columns already establish. `filter (where
/// closed_at is not null)` is the same "nothing to average yet" discipline
/// <c>AverageFirstResponseSeconds</c> already applies to its own null case: a conversation still open
/// contributes nothing to the average, rather than being treated as zero seconds or as "now minus
/// created" - either of which would keep changing the historical average every time the report re-runs
/// for the exact same window, purely because time passed.</item>
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
            select c.id, c.site_id, c.visitor_id, c.state, c.operator_id as assigned_operator_id,
                c.created_at, c.closed_at
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
                ms.first_operator_at,
                coalesce(ms.first_operator_id, iw.assigned_operator_id) as attributed_operator_id,
                iw.created_at,
                iw.closed_at
            from in_window iw
            left join lateral (
                select ci.kind
                from channel_identities ci
                where ci.site_id = iw.site_id and ci.visitor_id = iw.visitor_id and ci.active
                order by ci.first_seen_at asc
                limit 1
            ) ch on true
            left join lateral (
                select
                    min(m.created_at) filter (where m.author_kind = @VisitorAuthorKind) as first_visitor_at,
                    min(m.created_at) filter (where m.author_kind = @OperatorAuthorKind) as first_operator_at,
                    (array_agg(m.author_id order by m.created_at)
                        filter (where m.author_kind = @OperatorAuthorKind))[1] as first_operator_id
                from messages m
                where m.conversation_id = iw.id
            ) ms on true
        )
        select
            channel_label as "Channel",
            attributed_operator_id as "OperatorId",
            count(*) as "ConversationCount",
            count(*) filter (where state = @ClosedState and first_operator_at is null) as "MissedCount",
            (avg(extract(epoch from (first_operator_at - first_visitor_at)))
                filter (where first_operator_at is not null and first_visitor_at is not null))::double precision
                as "AverageFirstResponseSeconds",
            (avg(extract(epoch from (closed_at - created_at)))
                filter (where closed_at is not null))::double precision
                as "AverageDurationSeconds",
            grouping(attributed_operator_id) as "OperatorGrouping"
        from detail
        group by grouping sets ((), (channel_label), (attributed_operator_id))
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
        // `grouping(attributed_operator_id) == 1` is what tells this row apart from the per-operator
        // grouping set's own "nobody was ever assigned" bucket (the class doc comment's own remarks on
        // why `Channel is null` alone no longer uniquely identifies the total row once a third grouping
        // set exists).
        var overallRow = rows.SingleOrDefault(r => r.Channel is null && r.OperatorGrouping == 1);
        var overall = overallRow is null
            ? new OperatorAnalyticsBucket(0, null, null, 0)
            : ToBucket(overallRow);

        var byChannel = rows
            .Where(r => r.Channel is not null)
            .Select(r => new OperatorAnalyticsChannelBucket(r.Channel!, ToBucket(r)))
            .OrderBy(c => c.Channel, StringComparer.Ordinal)
            .ToList();

        // `OperatorGrouping == 0` selects the per-operator grouping set's own rows; `OperatorId is not
        // null` then drops that set's "nobody was ever assigned" bucket - there is no operator to report
        // it under (the class doc comment's own remarks on why this is an exclusion, not a placeholder).
        var byOperator = rows
            .Where(r => r.OperatorGrouping == 0 && r.OperatorId is not null)
            .Select(r => new OperatorAnalyticsOperatorBucket(new OperatorId(r.OperatorId!.Value), ToBucket(r)))
            .OrderBy(o => o.Operator.Value)
            .ToList();

        return new OperatorAnalyticsResult(overall, byChannel, byOperator);
    }

    private static OperatorAnalyticsBucket ToBucket(OperatorAnalyticsRow row) =>
        new(row.ConversationCount, row.AverageFirstResponseSeconds, row.AverageDurationSeconds, row.MissedCount);
}
