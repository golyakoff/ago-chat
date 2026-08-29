using Ago.Chat.Domain;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>One <c>messages</c> leaf partition, as reported by Postgres's own catalog rather than
/// computed from a clock - `MessagePartitionPruneJob` must only ever act on a partition that genuinely
/// exists, never on a name it merely expects to.</summary>
public sealed record MessagePartitionInfo(string Name, RetentionClass RetentionClass, DateOnly PeriodStart, DateOnly PeriodEnd);

/// <summary>
/// `15-04`: the read/drop half of partition pruning. Reads from <c>pg_partition_tree</c> - Postgres's
/// own recursive view of a partitioned table's whole subtree (available since Postgres 12; this
/// codebase runs 17) - rather than reconstructing partition names from a clock the way
/// <see cref="PartitionMaintenanceJob"/> does for *creating* them; a job that decides what to drop
/// should look at what actually exists, not what it thinks should exist.
///
/// <para><b>`13-06`/`adr/0031`: <c>pg_inherits</c> directly on <c>messages</c> stopped being the right
/// read the day this table grew a second partition level.</b> Verified against a real Postgres 17
/// while building this item: <c>SELECT ... FROM pg_inherits WHERE inhparent = 'messages'::regclass</c>
/// - this class's own query before this change - now returns only the three *class-level* partitions
/// (<c>messages_free</c>, <c>messages_starter</c>, <c>messages_growth</c>), never the monthly leaves
/// underneath them; every caller of this class (this job, <c>MessageSearchIndexJob</c>,
/// <c>MessageSiteIdBackfillJob</c>) would have silently stopped seeing any partition to act on at all,
/// failing closed (nothing dropped, nothing indexed, nothing backfilled) rather than loudly, which is
/// exactly the kind of regression that hides until someone notices the disk stopped shrinking.
/// <c>pg_partition_tree('messages')</c> returns the *whole* subtree regardless of depth, each row
/// carrying its own <c>isleaf</c> flag - filtering on that instead of a fixed inheritance depth is what
/// makes this correct at two levels today and would still be correct at three if a future item ever
/// added one.</para>
/// </summary>
public static class MessagePartitionPruneQuery
{
    public static async Task<IReadOnlyList<MessagePartitionInfo>> ListPartitionsAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT c.relname AS name
            FROM pg_partition_tree('messages'::regclass) pt
            JOIN pg_class c ON c.oid = pt.relid
            WHERE pt.isleaf
            ORDER BY name
            """;

        await using var command = new NpgsqlCommand(sql, connection);

        var partitions = new List<MessagePartitionInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            if (!MessagePartitionNames.TryParse(name, out var retentionClass, out var periodStart))
            {
                // Not one of this scheme's own monthly leaves - left alone rather than guessed at.
                continue;
            }

            partitions.Add(new MessagePartitionInfo(name, retentionClass, periodStart, periodStart.AddMonths(1)));
        }

        return partitions;
    }

    /// <summary>`13-06`: every distinct <c>attachment_id</c> a leaf partition's own rows reference,
    /// read before the partition is dropped - <c>MessagePartitionPruneJob</c>'s own source of truth for
    /// exactly which <c>attachments</c> rows belong to the messages about to disappear ("attachments
    /// expire with the messages they belong to", `adr/0031`'s Decision 4). Deliberately *not* a
    /// site+date-range query against the separate, unpartitioned `attachments` table: a site that
    /// changed tier mid-month can have two different retention classes' messages sharing the same
    /// calendar month, and a date-range predicate alone cannot tell which attachments belong to the
    /// partition actually being dropped versus a sibling class's partition that has not been archived
    /// yet - reading the exact ids straight off this partition's own rows cannot make that
    /// mistake.</summary>
    public static async Task<IReadOnlyList<Guid>> ListReferencedAttachmentIdsAsync(
        NpgsqlConnection connection, string partitionName, CancellationToken cancellationToken)
    {
        if (!MessagePartitionNames.TryParse(partitionName, out _, out _))
        {
            throw new ArgumentException($"'{partitionName}' is not a recognised messages partition name.", nameof(partitionName));
        }

        var sql = $"SELECT DISTINCT attachment_id FROM {partitionName} WHERE attachment_id IS NOT NULL";
        await using var command = new NpgsqlCommand(sql, connection);

        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    /// <summary>Idempotent by construction (<c>IF EXISTS</c>) - the same reason
    /// <see cref="PartitionMaintenanceJob"/>'s own <c>CREATE TABLE IF NOT EXISTS</c> is, for a second
    /// <c>Worker</c> replica racing this one on the same partition. <paramref name="partitionName"/>
    /// must already match <see cref="MessagePartitionNames.TryParse"/> - callers only ever pass a name
    /// this class itself returned from <see cref="ListPartitionsAsync"/>, never a caller-supplied
    /// string, but the assert stays as the same defense-in-depth <see cref="PartitionMaintenanceJob"/>
    /// applies to its own interpolated identifiers.</summary>
    public static async Task DropPartitionAsync(
        NpgsqlConnection connection, string partitionName, CancellationToken cancellationToken)
    {
        if (!MessagePartitionNames.TryParse(partitionName, out _, out _))
        {
            throw new ArgumentException($"'{partitionName}' is not a recognised messages partition name.", nameof(partitionName));
        }

        var sql = $"DROP TABLE IF EXISTS {partitionName};";
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
