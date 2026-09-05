using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `24-12`: raw Npgsql, not EF - <see cref="IAccessRecordRepository"/>'s own remarks explain why (no
/// aggregate, no invariant beyond "one row per event", the same reasoning
/// <see cref="ExportRequestRepository"/> already gives for itself).
/// </summary>
public sealed class AccessRecordRepository(NpgsqlDataSource dataSource) : IAccessRecordRepository
{
    public async Task RecordAsync(AccessRecordToWrite record, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            insert into access_records (id, occurred_at, access_kind, site_id, actor_kind, actor_id, resource_kind, resource_id)
            values (@id, @occurredAt, @accessKind, @siteId, @actorKind, @actorId, @resourceKind, @resourceId)
            """,
            connection);
        command.Parameters.AddWithValue("id", record.Id);
        command.Parameters.AddWithValue("occurredAt", record.OccurredAt);
        command.Parameters.AddWithValue("accessKind", record.AccessKind.ToString());
        // Explicit NpgsqlDbType on every nullable parameter: AddWithValue(DBNull.Value) alone gives
        // Npgsql nothing to infer a type from ("could not determine data type of parameter") the one
        // time a value is actually null - siteId (OwnerSiteList's own cross-tenant read), resourceKind
        // and resourceId (every access kind with no single named resource).
        command.Parameters.Add(new NpgsqlParameter("siteId", NpgsqlDbType.Uuid)
        {
            Value = (object?)record.SiteId?.Value ?? DBNull.Value,
        });
        command.Parameters.AddWithValue("actorKind", record.ActorKind.ToString());
        command.Parameters.AddWithValue("actorId", record.ActorId);
        command.Parameters.Add(new NpgsqlParameter("resourceKind", NpgsqlDbType.Text)
        {
            Value = (object?)record.ResourceKind?.ToString() ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("resourceId", NpgsqlDbType.Uuid)
        {
            Value = (object?)record.ResourceId ?? DBNull.Value,
        });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AccessRecordPage> ListForSiteAsync(
        SiteId siteId, Guid? beforeId, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select id, occurred_at, access_kind, actor_kind, actor_id, resource_kind, resource_id
            from access_records
            where site_id = @siteId and (@beforeId is null or id < @beforeId)
            order by id desc
            limit @limit
            """,
            connection);
        command.Parameters.AddWithValue("siteId", siteId.Value);
        command.Parameters.Add(new NpgsqlParameter("beforeId", NpgsqlDbType.Uuid)
        {
            Value = (object?)beforeId ?? DBNull.Value,
        });
        // One extra row, not returned - the same "ask for limit+1, use the (limit+1)th only to decide
        // whether a next page exists" shape every other keyset read in this codebase uses, so paging
        // needs no separate count query.
        command.Parameters.AddWithValue("limit", limit + 1);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<AccessRecordItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new AccessRecordItem(
                reader.GetGuid(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                Enum.Parse<AccessRecordKind>(reader.GetString(2)),
                Enum.Parse<AccessRecordActorKind>(reader.GetString(3)),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : Enum.Parse<AccessRecordResourceKind>(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetGuid(6)));
        }

        // The (limit+1)th row only ever proves a next page exists - it is never returned itself, and
        // the cursor for that next page is the last row actually handed back (the oldest one on this
        // page), not the lookahead row one further back.
        var hasMore = items.Count > limit;
        if (hasMore)
        {
            items.RemoveRange(limit, items.Count - limit);
        }

        var nextBeforeId = hasMore ? items[^1].Id : (Guid?)null;

        return new AccessRecordPage(items, nextBeforeId);
    }
}
