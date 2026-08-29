using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`13-06`: raw Npgsql, not EF - <see cref="IMessageArchiveRepository"/>'s own remarks explain
/// why, the same reasoning `ExportRequestRepository` already gives for the identical shape.</summary>
public sealed class MessageArchiveRepository(NpgsqlDataSource dataSource) : IMessageArchiveRepository
{
    public async Task RecordAsync(
        Guid id, SiteId siteId, RetentionClass retentionClass, DateOnly periodStart, DateOnly periodEnd,
        string objectKey, DateTimeOffset archivedAt, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        // ON CONFLICT DO NOTHING on the unique (site_id, retention_class, period_start) triple - a
        // retry after a crash mid-cycle (MessageArchiveJob's own remarks) must be a no-op, not a
        // constraint-violation exception the job would otherwise have to catch and swallow itself.
        await using var command = new NpgsqlCommand(
            """
            insert into message_archives (id, site_id, retention_class, period_start, period_end, object_key, archived_at)
            values (@id, @siteId, @retentionClass, @periodStart, @periodEnd, @objectKey, @archivedAt)
            on conflict (site_id, retention_class, period_start) do nothing
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("siteId", siteId.Value);
        command.Parameters.AddWithValue("retentionClass", retentionClass.Value);
        command.Parameters.AddWithValue("periodStart", periodStart);
        command.Parameters.AddWithValue("periodEnd", periodEnd);
        command.Parameters.AddWithValue("objectKey", objectKey);
        command.Parameters.AddWithValue("archivedAt", archivedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlySet<Guid>> ListArchivedSiteIdsAsync(
        RetentionClass retentionClass, DateOnly periodStart, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "select site_id from message_archives where retention_class = @retentionClass and period_start = @periodStart",
            connection);
        command.Parameters.AddWithValue("retentionClass", retentionClass.Value);
        command.Parameters.AddWithValue("periodStart", periodStart);

        var siteIds = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            siteIds.Add(reader.GetGuid(0));
        }

        return siteIds;
    }

    public async Task<IReadOnlyList<MessageArchiveRecord>> ListForSiteAsync(SiteId siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select id, retention_class, period_start, period_end, object_key, archived_at
            from message_archives
            where site_id = @siteId
            order by period_start desc, retention_class
            """,
            connection);
        command.Parameters.AddWithValue("siteId", siteId.Value);

        var records = new List<MessageArchiveRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new MessageArchiveRecord(
                reader.GetGuid(0), siteId, new RetentionClass(reader.GetString(1)),
                reader.GetFieldValue<DateOnly>(2), reader.GetFieldValue<DateOnly>(3),
                reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5)));
        }

        return records;
    }

    public async Task<MessageArchiveRecord?> GetAsync(
        SiteId siteId, RetentionClass retentionClass, DateOnly periodStart, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select id, period_end, object_key, archived_at
            from message_archives
            where site_id = @siteId and retention_class = @retentionClass and period_start = @periodStart
            """,
            connection);
        command.Parameters.AddWithValue("siteId", siteId.Value);
        command.Parameters.AddWithValue("retentionClass", retentionClass.Value);
        command.Parameters.AddWithValue("periodStart", periodStart);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MessageArchiveRecord(
            reader.GetGuid(0), siteId, retentionClass, periodStart,
            reader.GetFieldValue<DateOnly>(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3));
    }
}
