using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`22-08`: the Dapper adapter for <see cref="ISiteSuspensionReadStore"/> - the same
/// "hand-written SQL over the write model, never through the aggregate, never cached" shape
/// <see cref="EnabledModuleReadStore"/> already establishes for an entitlement's own live expiry check
/// (`CLAUDE.md` rule 8).</summary>
public sealed class SiteSuspensionReadStore(NpgsqlDataSource dataSource) : ISiteSuspensionReadStore
{
    private const string IsSuspendedSql =
        "select 1 from sites where id = @SiteId and suspended_until is not null and suspended_until > @Now";

    private const string ActiveSuspensionsSql =
        "select id as \"SiteId\" from sites where suspended_until is not null and suspended_until > @Now";

    // `ListForOwnerAsync`'s own "most recent act per site" read - a `DISTINCT ON` rather than a
    // window-function `ROW_NUMBER() OVER (...) = 1`, the smaller of two equally correct ways to
    // express "one row per site" for a query this codebase runs at console-poll frequency, never on a
    // hot path. `ix_site_suspensions_site_id_performed_at` is what makes the `ORDER BY` inside it an
    // index-order scan rather than a sort.
    private const string ForOwnerSql =
        """
        select s.id as "SiteId", s.name as "SiteName", s.suspended_until as "SuspendedUntil",
               a.performed_by as "LastActionBy", a.reason as "LastActionReason", a.performed_at as "LastActionAt"
        from sites s
        join lateral (
            select performed_by, reason, performed_at
            from site_suspensions
            where site_id = s.id
            order by performed_at desc
            limit 1
        ) a on true
        where s.suspended_until is not null and s.suspended_until > @Now
        order by s.suspended_until asc
        """;

    public async Task<bool> IsSuspendedAsync(SiteId siteId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            IsSuspendedSql, new { SiteId = siteId.Value, Now = now }, cancellationToken: cancellationToken));
        return row is not null;
    }

    public async Task<IReadOnlyList<SiteId>> ListActiveSuspensionsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<Guid>(new CommandDefinition(
            ActiveSuspensionsSql, new { Now = now }, cancellationToken: cancellationToken));
        return rows.Select(id => new SiteId(id)).ToList();
    }

    public async Task<IReadOnlyList<OwnerSuspensionSummary>> ListForOwnerAsync(
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<OwnerSuspensionRow>(new CommandDefinition(
            ForOwnerSql, new { Now = now }, cancellationToken: cancellationToken));

        return rows.Select(r => new OwnerSuspensionSummary(
            new SiteId(r.SiteId), r.SiteName, r.SuspendedUntil, r.LastActionBy, r.LastActionReason, r.LastActionAt))
            .ToList();
    }

    private sealed class OwnerSuspensionRow
    {
        public Guid SiteId { get; init; }

        public string SiteName { get; init; } = string.Empty;

        public DateTimeOffset SuspendedUntil { get; init; }

        public string LastActionBy { get; init; } = string.Empty;

        public string LastActionReason { get; init; } = string.Empty;

        public DateTimeOffset LastActionAt { get; init; }
    }
}
