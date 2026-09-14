using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `8-07`/`adr/0058`: the demo tenant lifecycle's read side - how many are alive, and which have
/// expired. The removal itself moved to <c>Ago.Chat.Infrastructure.Postgres.SiteErasurePublisher</c>
/// (`25-82`), which needs <c>AgoChatDbContext</c> to commit the site delete and an outbox row together;
/// this class stays on Dapper over a bare <c>NpgsqlDataSource</c> connection, safe to capture for the
/// whole lifetime of the singleton <c>DemoTenantExpiryJob</c> because neither read here carries any
/// per-operation state.
///
/// <para>Dapper rather than EF, matching `adr/0004`'s split: both reads return no aggregate.</para>
/// </summary>
public sealed class DemoTenantRepository(NpgsqlDataSource dataSource) : IDemoTenantRepository
{
    public async Task<int> CountLiveAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        // Served by ix_sites_demo_expiry, the partial index `Stage8AddSiteDemoExpiry` adds - partial on
        // `demo_expires_at IS NOT NULL`, so it is proportional to the demo tenants alive rather than to
        // every tenant that has ever registered. Same shape as `4-01`'s ix_conversations_waiting.
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "select count(*) from sites where demo_expires_at is not null and demo_expires_at > @now",
            new { now }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<ExpiredDemoTenant>> ListExpiredAsync(
        DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // The operators' subject ids come back with the site in one round trip, because the sweeper
        // needs them *after* it has deleted the rows that hold them - reading them separately would be
        // a second query racing its own delete.
        //
        // `external_subject_id` is nullable (adr/0022: unique when present), and a demo operator always
        // has one - but `array_remove(..., null)` is cheap insurance against a hand-seeded row, and the
        // alternative is a null element the caller would have to filter anyway.
        const string sql = """
            select s.id                                                    as SiteId,
                   s.public_key                                            as PublicKey,
                   s.demo_expires_at                                       as ExpiredAt,
                   coalesce(array_remove(array_agg(o.external_subject_id), null), '{}') as ExternalSubjectIds
            from sites s
            left join operators o on o.site_id = s.id
            where s.demo_expires_at is not null and s.demo_expires_at <= @now
            group by s.id, s.public_key, s.demo_expires_at
            order by s.demo_expires_at
            limit @limit
            """;

        var rows = await connection.QueryAsync<ExpiredDemoTenantRow>(new CommandDefinition(
            sql, new { now, limit }, cancellationToken: cancellationToken));

        return [.. rows.Select(r => new ExpiredDemoTenant(
            new SiteId(r.SiteId), r.PublicKey, r.ExpiredAt, r.ExternalSubjectIds))];
    }

    public async Task<IReadOnlyList<string>> ListAttachmentObjectKeysAsync(
        SiteId siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        // Both keys per attachment, not just the object: `5-04` stores a thumbnail beside every image,
        // and `personal-data.md` records that "deleting a conversation cascades the attachments rows and
        // leaves the MinIO objects behind" is an existing gap. This is the one place in the codebase
        // that does not have it.
        var keys = await connection.QueryAsync<string?>(new CommandDefinition(
            """
            select object_key from attachments where site_id = @siteId
            union all
            select thumbnail_key from attachments where site_id = @siteId and thumbnail_key is not null
            """,
            new { siteId = siteId.Value }, cancellationToken: cancellationToken));

        return [.. keys.Where(k => !string.IsNullOrEmpty(k)).Select(k => k!)];
    }

    // Dapper materialises into this rather than straight into the record: Guid[] needs a settable
    // property of its own type, and ExpiredDemoTenant holds an IReadOnlyList.
    private sealed class ExpiredDemoTenantRow
    {
        public Guid SiteId { get; init; }

        public string PublicKey { get; init; } = string.Empty;

        public DateTimeOffset ExpiredAt { get; init; }

        public string[] ExternalSubjectIds { get; init; } = [];
    }
}
