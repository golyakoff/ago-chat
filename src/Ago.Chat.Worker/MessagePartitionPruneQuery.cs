using Ago.Chat.Domain;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>One (site, retention class, month) slice of `messages` whose rows are past the retention
/// horizon - the unit `MessagePartitionPruneJob`/`MessageArchiveJob` now both work in, replacing the
/// whole-partition unit the pre-`15-09` scheme used. <see cref="BucketName"/> is the leaf partition this
/// slice's rows happen to live in (kept only so <see cref="MessagePartitionPruneQuery.DeleteMessageBatchAsync"/>'s
/// own bounded loop reads/writes against the same connection efficiently within one bucket - it plays no
/// role in identifying the slice, which is fully named by the other four fields).</summary>
public sealed record ExpiredMessageSlice(
    string BucketName, Guid SiteId, RetentionClass RetentionClass, DateOnly PeriodStart, DateOnly PeriodEnd);

/// <summary>
/// `15-04`'s own discovery/removal query, reworked for `15-09`/`adr/0087`: `messages` no longer has a
/// physical partition per (retention class, month), so there is nothing left to enumerate via
/// `pg_partition_tree` and nothing left to `DROP`. The removal unit is now a slice of rows identified by
/// value - `(site_id, retention_class, month)` - discovered by querying, not by reading Postgres's
/// catalog, and removed by a bounded `DELETE ... WHERE`.
///
/// <para><b>Bucket iteration is a fixed, in-memory list now, not a catalog read.</b>
/// <see cref="MessagePartitionNames.AllBucketNames"/> - 64 names, known at compile time - replaces the
/// live `pg_partition_tree` read this class used before `15-09`: the bucket list can no longer change
/// (`adr/0087`'s own "64, forever" decision), so there is nothing dynamic left to ask Postgres
/// about.</para>
///
/// <para><b>Discovery is per-bucket, deliberately bounded the same way the old per-partition iteration
/// was</b> - <see cref="ListExpiredSlicesAsync"/> scans one of the 64 buckets at a time rather than the
/// whole `messages` table in one query, so no single statement's working set is larger than 1/64th of
/// the table. Removal (<see cref="DeleteMessageBatchAsync"/>) and the attachment lookup that precedes it
/// (<see cref="ListReferencedAttachmentIdsAsync"/>) address the *parent* `messages` table with a full
/// `(site_id, retention_class, created_at)` predicate instead of the named bucket, deliberately: once a
/// slice's exact `site_id` is known, Postgres prunes to the one bucket that predicate can possibly match
/// on its own, so there is no need to interpolate a bucket name into either statement at all - the
/// pruning this whole item exists to buy is what makes that safe and correct.</para>
/// </summary>
public static class MessagePartitionPruneQuery
{
    /// <summary>Every distinct (site, retention class, month) combination this one bucket holds whose
    /// rows are entirely before <paramref name="cutoff"/> - `MessagePartitionPruneJob`'s and
    /// `MessageArchiveJob`'s shared discovery step. `date_trunc('month', ...)` matches the calendar-month
    /// grouping the pre-`15-09` scheme's own leaf partitions used, so an archive object's own "one period
    /// = one calendar month" meaning is unchanged.</summary>
    public static async Task<IReadOnlyList<ExpiredMessageSlice>> ListExpiredSlicesAsync(
        NpgsqlConnection connection, string bucketName, DateOnly cutoff, CancellationToken cancellationToken)
    {
        var sql = $"""
            select site_id, retention_class, date_trunc('month', created_at)::date as period_start
            from {bucketName}
            where created_at < @cutoff
            group by site_id, retention_class, date_trunc('month', created_at)
            order by period_start
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("cutoff", cutoff.ToDateTime(TimeOnly.MinValue));

        var slices = new List<ExpiredMessageSlice>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var siteId = reader.GetGuid(0);
            var retentionClass = new RetentionClass(reader.GetString(1));
            var periodStart = reader.GetFieldValue<DateOnly>(2);
            slices.Add(new ExpiredMessageSlice(bucketName, siteId, retentionClass, periodStart, periodStart.AddMonths(1)));
        }

        return slices;
    }

    /// <summary>Every distinct `attachment_id` a slice's own rows reference, read before any row in it
    /// is deleted - `MessagePartitionPruneJob`'s own source of truth for exactly which `attachments`
    /// rows belong to the messages about to disappear ("attachments expire with the messages they
    /// belong to", `adr/0031`'s Decision 4). Queries the parent `messages` table - see this class's own
    /// remarks for why that prunes to one bucket without needing a bucket name.</summary>
    public static async Task<IReadOnlyList<Guid>> ListReferencedAttachmentIdsAsync(
        NpgsqlConnection connection, ExpiredMessageSlice slice, CancellationToken cancellationToken)
    {
        const string sql = """
            select distinct attachment_id
            from messages
            where site_id = @siteId and retention_class = @retentionClass
              and created_at >= @periodStart and created_at < @periodEnd
              and attachment_id is not null
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        AddSliceParameters(command, slice);

        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    /// <summary>One bounded batch of a slice's own removal - `FOR UPDATE SKIP LOCKED`, the same shape
    /// `ConversationErasureQuery.DeleteMessageBatchAsync` already establishes for a per-conversation
    /// delete, applied here to a per-(site, class, month) one. `MessagePartitionPruneJob` loops this
    /// until a call returns fewer rows than <paramref name="batchSize"/>, `MessageSiteIdBackfillJob`'s
    /// own "drain one unit completely before moving to the next" shape.</summary>
    public static async Task<int> DeleteMessageBatchAsync(
        NpgsqlConnection connection, ExpiredMessageSlice slice, int batchSize, CancellationToken cancellationToken)
    {
        const string sql = """
            delete from messages
            where site_id = @siteId
              and id in (
                select id from messages
                where site_id = @siteId and retention_class = @retentionClass
                  and created_at >= @periodStart and created_at < @periodEnd
                limit @batchSize
                for update skip locked
            )
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        AddSliceParameters(command, slice);
        command.Parameters.AddWithValue("batchSize", batchSize);

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddSliceParameters(NpgsqlCommand command, ExpiredMessageSlice slice)
    {
        command.Parameters.AddWithValue("siteId", slice.SiteId);
        command.Parameters.AddWithValue("retentionClass", slice.RetentionClass.Value);
        command.Parameters.AddWithValue("periodStart", slice.PeriodStart.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("periodEnd", slice.PeriodEnd.ToDateTime(TimeOnly.MinValue));
    }
}
