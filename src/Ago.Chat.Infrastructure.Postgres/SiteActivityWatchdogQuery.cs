using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `23-73`: the raw SQL half of the inactivity watchdog's own reset - one conditional <c>UPDATE</c>,
/// callable either against a connection that owns no transaction of its own
/// (<see cref="SiteActivityWatchdogRepository"/>, the login/claims-transformation hook) or against an
/// already-open one it must enlist in rather than fight
/// (<see cref="Pipeline.MessageBatchWriter"/>, the operator-outbound-message hook - the same
/// "pass the ambient transaction in explicitly" shape raw <see cref="NpgsqlCommand"/> use always needs
/// once an <see cref="NpgsqlTransaction"/> is already open on the connection, since Npgsql does not
/// infer one).
///
/// <para><b>One statement, not read-then-write.</b> <c>WHERE (last_operator_activity_at IS NULL OR
/// last_operator_activity_at &lt; @at - @minInterval)</c> makes the throttle and the update the same
/// round trip: a call inside <see cref="SiteActivityWatchdogOptions.MinTouchInterval"/> of the last
/// recorded touch matches no row and writes nothing (a cheap indexed-by-primary-key no-op, not a
/// wasted row version), while a call past it both advances the timestamp and clears
/// <c>inactivity_warning_sent_at</c> in the same write - a fresh reset starts a fresh three-month
/// cycle, so any warning sent against the cycle just reset must not survive into the new one (see
/// <c>InactivityWatchdogQuery.ListWarningCandidatesAsync</c>'s own remarks on why that flag being
/// clear is what lets a genuinely-reactivated site be warned again if it later goes quiet a second
/// time).</para>
/// </summary>
public static class SiteActivityWatchdogQuery
{
    private const string TouchOneSql = """
        update sites
        set last_operator_activity_at = @at,
            inactivity_warning_sent_at = null
        where id = @siteId
          and (last_operator_activity_at is null or last_operator_activity_at < @at - @minInterval)
        """;

    public static async Task<int> TouchAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid siteId, DateTimeOffset at,
        TimeSpan minInterval, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(TouchOneSql, connection, transaction);
        command.Parameters.AddWithValue("siteId", siteId);
        command.Parameters.AddWithValue("at", at);
        command.Parameters.AddWithValue("minInterval", minInterval);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// The batched form <see cref="Pipeline.MessageBatchWriter"/> uses - one flush can carry operator
    /// messages for several conversations across several different sites, so this touches every one of
    /// them in a single statement rather than one round trip per site, the same "batch it, don't loop
    /// a single-row statement" shape <c>AttachmentOrphanSweepQuery</c>'s own release-aggregation
    /// already uses for a different table in the same spirit.
    /// </summary>
    public static async Task<int> TouchManyAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, IReadOnlyCollection<Guid> siteIds,
        DateTimeOffset at, TimeSpan minInterval, CancellationToken cancellationToken)
    {
        if (siteIds.Count == 0)
        {
            return 0;
        }

        const string sql = """
            update sites
            set last_operator_activity_at = @at,
                inactivity_warning_sent_at = null
            where id = any(@siteIds)
              and (last_operator_activity_at is null or last_operator_activity_at < @at - @minInterval)
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("siteIds", siteIds.ToArray());
        command.Parameters.AddWithValue("at", at);
        command.Parameters.AddWithValue("minInterval", minInterval);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
