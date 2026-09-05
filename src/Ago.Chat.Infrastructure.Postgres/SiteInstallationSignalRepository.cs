using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `23-06`: raw Npgsql for the two writes, Dapper for the read - <see cref="ISiteInstallationSignalRepository"/>'s
/// own remarks explain why this deliberately bypasses <see cref="Site"/>'s usual aggregate
/// load-mutate-save, the same "reaches a row without going through its aggregate" shape
/// <see cref="ErasureRequestRepository"/> already established.
/// </summary>
public sealed class SiteInstallationSignalRepository(NpgsqlDataSource dataSource) : ISiteInstallationSignalRepository
{
    // `23-06`'s own Scope: "At most one row write per site per minute". The WHERE clause is what makes
    // this cheap under load - every mint or renewal call still executes this statement, but Postgres
    // only *writes* the row (and only then does an index/heap update, WAL record, and eventually an
    // autovacuum-relevant dead tuple) when the condition matches. Under steady traffic from one site
    // that is one real write per minute, not one per visitor - the rest are a parsed, planned, and
    // immediately-false WHERE evaluation against an already-open connection, which is the entire cost
    // this throttle is paying to avoid turning a hot read-mostly path into a write-per-request one.
    public async Task RecordSightingAsync(SiteId siteId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            update sites
            set last_seen_at = @now,
                first_seen_at = coalesce(first_seen_at, @now)
            where id = @siteId
              and (last_seen_at is null or last_seen_at < @now - interval '1 minute')
            """,
            connection);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("siteId", siteId.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // The identical once-a-minute throttle as RecordSightingAsync above, and for the identical reason
    // stated there - not merely for symmetry. The classic failure this column exists to catch (a
    // `www.` vs. bare-domain mismatch) means *every* request from a broken tenant's real traffic hits
    // this exact branch, so an unthrottled write here would be exactly as hot as an unthrottled write
    // on the success path would have been.
    public async Task RecordRefusedOriginAsync(SiteId siteId, string origin, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            update sites
            set last_refused_origin = @origin,
                last_refused_origin_at = @now
            where id = @siteId
              and (last_refused_origin_at is null or last_refused_origin_at < @now - interval '1 minute')
            """,
            connection);
        command.Parameters.AddWithValue("origin", origin);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("siteId", siteId.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SiteInstallationSignals> GetAsync(SiteId siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<SiteInstallationSignalsRow>(
            new CommandDefinition(
                """
                select first_seen_at as "FirstSeenAt", last_seen_at as "LastSeenAt",
                       last_refused_origin as "LastRefusedOrigin", last_refused_origin_at as "LastRefusedOriginAt"
                from sites
                where id = @SiteId
                """,
                new { SiteId = siteId.Value },
                cancellationToken: cancellationToken));

        return row is null
            ? SiteInstallationSignals.None
            : new SiteInstallationSignals(
                ToUtcOffset(row.FirstSeenAt), ToUtcOffset(row.LastSeenAt), row.LastRefusedOrigin, ToUtcOffset(row.LastRefusedOriginAt));
    }

    private static DateTimeOffset? ToUtcOffset(DateTime? value) =>
        value is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)) : null;
}
