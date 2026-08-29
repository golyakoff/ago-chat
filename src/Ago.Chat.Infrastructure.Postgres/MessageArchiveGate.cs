using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `13-06`/`adr/0031`: the real <see cref="IMessageArchiveGate"/> - replaces
/// <c>AlwaysConfirmedMessageArchiveGate</c> (`15-04`'s own stand-in) now that this item exists. Confirms
/// a partition only once **every** distinct site whose messages it holds has a matching row in
/// `message_archives` - the manifest `Ago.Chat.Worker`'s `MessageArchiveJob` writes only after a real
/// upload to object storage has already succeeded. This class does no archiving itself and touches no
/// object storage - it answers one question against Postgres alone, the same "the port's real
/// implementation reads whatever it needs out of its own archive-manifest table" shape
/// <see cref="IMessageArchiveGate"/>'s own doc comment anticipated when `15-04` declared the port with
/// no implementation to back it.
///
/// <para><b>A partition with any row still missing `site_id` is never confirmed, on purpose.</b>
/// `MessageArchiveJob` archives a site's messages by querying `WHERE site_id = @siteId`
/// (`18-01`'s denormalized column) - a row `MessageSiteIdBackfillJob` has not reached yet cannot be
/// attributed to any site's archive, and would be silently lost forever if the partition it lives in
/// were ever dropped while it still had no owner. Treating "any NULL site_id remaining" as
/// "not archived" turns that into backpressure instead of data loss: the partition simply stays past
/// its retention horizon, undropped, until the backfill converges - exactly the same
/// "leave it in place and log, rather than guess" shape `MessagePartitionPruneJob` already applies to
/// an unconfirmed partition for any other reason.</para>
///
/// <para><paramref name="partitionName"/> is parsed back into its own (class, period) via
/// <see cref="MessagePartitionNames.TryParse"/> - the exact "a real implementation reads whatever it
/// needs out of the name" the port's own doc comment named as the point of keeping the name whole
/// rather than widening the interface.</para>
/// </summary>
public sealed class MessageArchiveGate(NpgsqlDataSource dataSource) : IMessageArchiveGate
{
    public async Task<bool> IsArchivedAsync(
        string partitionName, DateOnly periodStart, DateOnly periodEnd, CancellationToken cancellationToken)
    {
        if (!MessagePartitionNames.TryParse(partitionName, out var retentionClass, out var parsedPeriodStart) ||
            parsedPeriodStart != periodStart)
        {
            // Defence-in-depth, matching MessagePartitionPruneQuery.DropPartitionAsync's own assert -
            // callers only ever pass a name/period pair this codebase's own catalog read produced.
            throw new ArgumentException($"'{partitionName}' is not a recognised messages partition name.", nameof(partitionName));
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // True iff (a) no row in the partition still has a NULL site_id, and (b) every distinct
        // site_id present has a matching message_archives row for this exact (class, period). One
        // statement, one round trip - the partition table name is interpolated (identical trust chain
        // to DropPartitionAsync's own: only ever a name this class itself just validated above, never
        // caller-supplied).
        var sql = $"""
            select not exists (
                select 1 from {partitionName} where site_id is null
                union all
                select 1
                from (select distinct site_id from {partitionName} where site_id is not null) p
                where not exists (
                    select 1 from message_archives a
                    where a.site_id = p.site_id and a.retention_class = @retentionClass and a.period_start = @periodStart
                )
            )
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("retentionClass", retentionClass.Value);
        command.Parameters.AddWithValue("periodStart", periodStart);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is true;
    }
}
