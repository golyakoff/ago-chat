using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `8-07`/`adr/0058`: the demo tenant lifecycle's data access, and - in <see cref="DeleteSiteAsync"/> -
/// the narrow erasure this item builds because `16-02` is scoped and not built.
///
/// <para>Dapper rather than EF, matching `adr/0004`'s split: <see cref="CountLiveAsync"/> and
/// <see cref="ListExpiredAsync"/> are reads that return no aggregate, and the delete is one statement
/// that deliberately reaches rows belonging to several aggregates - which is the one thing an EF
/// change-tracked save must never do.</para>
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

    /// <summary>
    /// <b>One statement, and what it reaches is a property of the schema rather than of this method.</b>
    /// Every table that holds a demo tenant's data carries a foreign key to `sites` with
    /// `ON DELETE CASCADE` (EF's default for a required relationship, which is what every one of these
    /// is): `visitors`, `conversations` - and `messages` through it - `attachments`,
    /// `channel_identities`, `operators`, `operator_roles` through them, `roles`, `webhook_endpoints`
    /// and `webhook_deliveries` through them.
    ///
    /// <para>That is why this is one line and not a hand-ordered sequence of deletes: a list written
    /// here would be a second, weaker copy of the schema, and it would silently stop being complete the
    /// first time somebody adds a table - which is exactly how erasure becomes partial. The integration
    /// test asserts emptiness table by table against `personal-data.md`'s own list rather than trusting
    /// this comment.</para>
    ///
    /// <para><b>What it does not reach</b>, stated here because a deletion that quietly misses
    /// something is worse than one that says what it misses (`adr/0058` has the full account): the
    /// object store and the identity provider, both handled by the caller because neither can join this
    /// transaction; `outbox` rows, which are body-free by contract but do carry this site's ids;
    /// backups, until `15-02`'s retention window ages them out; and any node queue, trace or log line.
    /// None of those is reachable from a `DELETE`, and pretending otherwise is the failure mode this
    /// paragraph exists to prevent.</para>
    /// </summary>
    public async Task DeleteSiteAsync(SiteId siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "delete from sites where id = @siteId",
            new { siteId = siteId.Value }, cancellationToken: cancellationToken));
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
