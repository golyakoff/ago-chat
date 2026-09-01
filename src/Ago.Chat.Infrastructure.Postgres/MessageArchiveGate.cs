using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `13-06`/`adr/0031`, simplified for `15-09`/`adr/0087`: the real <see cref="IMessageArchiveGate"/> -
/// replaces <c>AlwaysConfirmedMessageArchiveGate</c> (`15-04`'s own stand-in). Confirms one tenant's
/// (retention class, period) slice only once it has a matching row in `message_archives` - the manifest
/// `Ago.Chat.Worker`'s `MessageArchiveJob` writes only after a real upload to object storage has already
/// succeeded. This class does no archiving itself and touches no object storage - it answers one
/// question against Postgres alone.
///
/// <para><b>One row, not a whole-partition aggregate.</b> Before `15-09`, one partition held every
/// tenant's rows for a (class, period), so this class had to check *every* distinct `site_id` the
/// partition held against the manifest, and refuse confirmation if even one had a `NULL` `site_id`
/// (`MessageSiteIdBackfillJob`'s own backpressure). Neither concern survives `15-09`: `site_id` is now
/// `NOT NULL` everywhere (closed by the repartitioning migration itself, `Message.SiteId`'s own remarks),
/// and the caller (`MessagePartitionPruneJob`) already knows exactly which site's slice it is asking
/// about, because its own discovery query produces `(site_id, retention_class, period)` tuples directly
/// rather than "every site somewhere in this partition." The question this gate answers is now a single
/// `EXISTS`, not an aggregate over an unknown set.</para>
/// </summary>
public sealed class MessageArchiveGate(NpgsqlDataSource dataSource) : IMessageArchiveGate
{
    public async Task<bool> IsArchivedAsync(
        SiteId siteId, RetentionClass retentionClass, DateOnly periodStart, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = """
            select exists(
                select 1 from message_archives
                where site_id = @siteId and retention_class = @retentionClass and period_start = @periodStart
            )
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("siteId", siteId.Value);
        command.Parameters.AddWithValue("retentionClass", retentionClass.Value);
        command.Parameters.AddWithValue("periodStart", periodStart);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is true;
    }
}
