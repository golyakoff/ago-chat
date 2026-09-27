using Ago.Chat.Domain;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `26-237`/`adr/0186` (operator-load rollup, decision B): the "first operator reply within one closing
/// interval" read that <c>ConversationAssignmentLog</c> stamps onto the <c>ConversationAssignmentClosed</c>
/// analytics event. The identical <c>replied</c> subquery <c>OperatorLoadReportReadStore</c> runs live
/// today, lifted to a single directly-tested query so the close path can resolve it once per interval
/// rather than the report recomputing it O(intervals) on every load - the same "no port, no Application
/// caller, tested standalone beside the table's other Postgres-shaped code" shape
/// <see cref="ConversationAssignmentOverlapQuery"/> already established for the overlap count.
///
/// <para><b>The reply is scoped to this operator, this conversation, this holding period.</b> The first
/// message this same operator sent in this conversation, no earlier than the interval's own
/// <c>started_at</c> and strictly before its <c>ended_at</c> - never a different operator's reply, never
/// one outside this interval (the live store's own rule, restated here). No match returns null, which the
/// event carries verbatim as "no reply from this operator during this interval" - never a sentinel.</para>
/// </summary>
public static class ConversationAssignmentReplyQuery
{
    private static readonly string OperatorAuthorKind = nameof(MessageAuthorKind.Operator);

    /// <summary>The instant of the first operator reply in <c>[startedAt, endedAt)</c> for
    /// <paramref name="operatorId"/> in <paramref name="conversationId"/>, or null if this operator sent
    /// no message in that window.</summary>
    public static async Task<DateTimeOffset?> FirstOperatorReplyAtAsync(
        NpgsqlDataSource dataSource,
        SiteId siteId,
        ConversationId conversationId,
        OperatorId operatorId,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT min(m.created_at)
            FROM messages m
            WHERE m.conversation_id = @conversationId
              AND m.site_id = @siteId
              AND m.author_kind = @authorKind
              AND m.author_id = @operatorId
              AND m.created_at >= @startedAt
              AND m.created_at < @endedAt
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("conversationId", conversationId.Value);
        command.Parameters.AddWithValue("siteId", siteId.Value);
        command.Parameters.AddWithValue("authorKind", OperatorAuthorKind);
        command.Parameters.AddWithValue("operatorId", operatorId.Value);
        command.Parameters.AddWithValue("startedAt", startedAt);
        command.Parameters.AddWithValue("endedAt", endedAt);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is DateTime dateTime
            ? new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc))
            : null;
    }
}
