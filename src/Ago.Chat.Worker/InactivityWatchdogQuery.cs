using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>`23-73`: one warning candidate - a site whose watchdog has gone quiet long enough to warn,
/// not yet warned this cycle, and not already flagged for erasure.</summary>
/// <param name="EffectiveActivityAt"><c>coalesce(last_operator_activity_at, created_at)</c>, never
/// <see langword="null"/> in practice for the identical reason
/// <see cref="Ago.Chat.Worker.PendingSiteErasure"/>'s own <c>RequestedAt</c> is not re-asserted here -
/// the query's own <c>WHERE</c> clause is the proof. See <see cref="InactivityWatchdogQuery"/>'s own
/// remarks on why this is a coalesce, not the bare column.</param>
public sealed record InactivityWarningCandidate(Guid SiteId, string SiteName, DateTimeOffset EffectiveActivityAt);

/// <summary>
/// `23-73`: the raw SQL half of <see cref="InactivityWatchdogJob"/> - the same "Worker job, raw Npgsql,
/// no Application-layer handler" shape <see cref="SiteErasureQuery"/>/<see cref="DemoTenantExpiryJob"/>'s
/// own dependency (<c>IDemoTenantRepository</c>, called directly rather than through a permission-
/// checked handler) already establish for a background sweep with no operator behind it - see
/// <see cref="OperatorId.System"/>'s own remarks for the identical reasoning applied to the erasure half
/// specifically.
/// </summary>
public static class InactivityWatchdogQuery
{
    /// <summary>
    /// <b>Why <c>coalesce(last_operator_activity_at, created_at)</c>, never the bare column alone.</b>
    /// A site created after this migration shipped starts with <c>last_operator_activity_at is null</c>
    /// - nothing sets it at creation, only a real reset (`23-73`'s two hooks) ever does. Requiring the
    /// bare column to be non-null would mean a site whose visitors write but whose operator never
    /// replies and never signs in - the exact case this backlog item's own Done-when names ("shown not
    /// deleting an account whose widget is in active use" is the mirror image of this: an account with
    /// *no* real use must still eventually go) - would never become a candidate at all, staying exempt
    /// forever purely because it was never touched even once. Falling back to
    /// <see cref="Ago.Chat.Domain.Site.CreatedAt"/> makes a fresh, always-inbound site start its own
    /// three-month clock from the moment it was
    /// created, exactly like every pre-existing site's own migration-time backfill already does
    /// (`Stage23AddSiteActivityWatchdog`'s own remarks) - the identical policy applied at two different
    /// moments (migration for old rows, creation for new ones) rather than one rule for rows that
    /// existed before this feature and a silently different one for rows created after it.
    /// </summary>
    private const string EffectiveActivityExpression = "coalesce(last_operator_activity_at, created_at)";

    /// <summary>
    /// A site is a warning candidate when its watchdog fell silent between
    /// <c>InactivityWindow - WarningLeadTime</c> and <c>InactivityWindow</c> ago - the window this
    /// job's own <see cref="InactivityWatchdogJobOptions.WarningLeadTime"/> exists to size - and it has
    /// not been warned since its watchdog's own last reset (<c>inactivity_warning_sent_at is null</c> -
    /// <see cref="Ago.Chat.Infrastructure.Postgres.SiteActivityWatchdogQuery"/>'s own remarks on why
    /// every reset clears this flag back to null, which is exactly what lets a site that goes quiet a
    /// second time be warned again rather than staying silently exempt forever). Also excludes a site
    /// already flagged for erasure (<c>erasure_requested_at is not null</c>) - once erasure is
    /// requested there is nothing left this warning could accomplish, and the site is about to
    /// disappear from every read path anyway.
    /// </summary>
    public static async Task<IReadOnlyList<InactivityWarningCandidate>> ListWarningCandidatesAsync(
        NpgsqlConnection connection, DateTimeOffset now, TimeSpan inactivityWindow, TimeSpan warningLeadTime,
        int limit, CancellationToken cancellationToken)
    {
        var sql = $"""
            select id, name, {EffectiveActivityExpression} as effective_activity_at
            from sites
            where erasure_requested_at is null
              and inactivity_warning_sent_at is null
              and {EffectiveActivityExpression} is not null
              and {EffectiveActivityExpression} <= @warnAt
              and {EffectiveActivityExpression} > @expireAt
            order by {EffectiveActivityExpression}
            limit @limit
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("warnAt", now - (inactivityWindow - warningLeadTime));
        command.Parameters.AddWithValue("expireAt", now - inactivityWindow);
        command.Parameters.AddWithValue("limit", limit);

        var candidates = new List<InactivityWarningCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new InactivityWarningCandidate(
                reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2)));
        }

        return candidates;
    }

    /// <summary>Idempotent: a second call for an already-warned site (<c>inactivity_warning_sent_at</c>
    /// already set) updates nothing - the flag names *when this cycle's* warning went out, not "how
    /// many times", and <see cref="ListWarningCandidatesAsync"/>'s own filter already keeps an
    /// already-warned site from being reconsidered, so this guard is a second, cheap backstop against
    /// the same double-send rather than the only thing preventing it.</summary>
    public static async Task<int> MarkWarnedAsync(
        NpgsqlConnection connection, Guid siteId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "update sites set inactivity_warning_sent_at = @at where id = @siteId and inactivity_warning_sent_at is null",
            connection);
        command.Parameters.AddWithValue("at", at);
        command.Parameters.AddWithValue("siteId", siteId);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Every non-removed operator email on this site, for the warning mail's own recipient
    /// list - a site with no operator email on file (never signed in with a real Keycloak identity,
    /// the demo-seed path, for instance) gets an empty list back, and
    /// <see cref="InactivityWatchdogJob"/>'s own caller still marks it warned rather than retrying
    /// forever for a mailbox that will never exist.</summary>
    public static async Task<IReadOnlyList<string>> ListOperatorEmailsAsync(
        NpgsqlConnection connection, Guid siteId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select email from operators where site_id = @siteId and removed_at is null and email is not null",
            connection);
        command.Parameters.AddWithValue("siteId", siteId);

        var emails = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            emails.Add(reader.GetString(0));
        }

        return emails;
    }

    /// <summary>A site is due for erasure once its watchdog has gone silent for the full
    /// <see cref="InactivityWatchdogJobOptions.InactivityWindow"/> - independent of whether it was ever
    /// warned (a warning that failed to send, or a deployment where mail was never configured, must not
    /// be what stands between an account and the policy `23-73`'s own "Answered" section states
    /// unconditionally). Already-flagged sites are excluded the same way
    /// <see cref="ListWarningCandidatesAsync"/> excludes them, for the same reason.</summary>
    public static async Task<IReadOnlyList<Guid>> ListExpiredAsync(
        NpgsqlConnection connection, DateTimeOffset now, TimeSpan inactivityWindow, int limit,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            select id
            from sites
            where erasure_requested_at is null
              and {EffectiveActivityExpression} is not null
              and {EffectiveActivityExpression} <= @expireAt
            order by {EffectiveActivityExpression}
            limit @limit
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("expireAt", now - inactivityWindow);
        command.Parameters.AddWithValue("limit", limit);

        var expired = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            expired.Add(reader.GetGuid(0));
        }

        return expired;
    }
}
