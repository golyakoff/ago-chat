using Ago.Chat.Domain;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `23-03`'s own proof that `conversation_assignments` is shaped for the reason it exists:
/// "concurrency at any instant is an interval overlap" (`decisions.md` §2). Raw Npgsql, following
/// `Ago.Chat.Worker`'s own `WaitingConversationClaimQuery` precedent for exactly this situation - "no
/// caller yet ... tested standalone here" - rather than a method on <c>IConversationAssignmentLog</c>:
/// that port is shaped by its real Application-layer callers (its own remarks), and this query has
/// none. `23-17`/`23-18` are the real callers the backlog item names; until one of them lands this
/// stays a directly-tested Infrastructure query rather than a port method Application would have to
/// expose with nothing behind it to call it.
///
/// <para>Kept in <c>Ago.Chat.Infrastructure.Postgres</c>, not <c>Ago.Chat.Worker</c> like
/// <c>WaitingConversationClaimQuery</c>: that query's placement follows its real caller (a
/// <c>Ago.Chat.Worker</c> job), and this one has no caller to follow, so it lives beside the table's
/// other Postgres-shaped code (<see cref="ConversationAssignmentLog"/>) instead.</para>
/// </summary>
public static class ConversationAssignmentOverlapQuery
{
    /// <summary>How many intervals for <paramref name="operatorId"/> were open at
    /// <paramref name="instant"/> - <c>started_at &lt;= instant</c> and either still open
    /// (<c>ended_at is null</c>) or ended strictly after it. An interval that ends exactly at
    /// <paramref name="instant"/> does not count: the operator had already stopped holding it by
    /// then, the same half-open-interval convention <c>[started_at, ended_at)</c> that makes two
    /// adjacent intervals (a transfer's own close-then-open, stamped with the identical instant) never
    /// overlap.</summary>
    public static async Task<int> CountHeldAtAsync(
        NpgsqlDataSource dataSource, OperatorId operatorId, DateTimeOffset instant, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(*)
            FROM conversation_assignments
            WHERE operator_id = @operatorId
              AND started_at <= @instant
              AND (ended_at IS NULL OR ended_at > @instant)
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("operatorId", operatorId.Value);
        command.Parameters.AddWithValue("instant", instant);

        var count = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(count);
    }
}
