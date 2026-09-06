using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `23-11`: raw Npgsql, not EF - <see cref="IContactRevealRepository"/>'s own remarks explain why (no
/// aggregate, no invariant beyond "one row per event", the same reasoning
/// <c>AccessRecordRepository</c> already gives for itself).
/// </summary>
public sealed class ContactRevealRepository(NpgsqlDataSource dataSource) : IContactRevealRepository
{
    public async Task RecordAsync(ContactRevealToWrite reveal, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            insert into contact_reveals (id, occurred_at, site_id, conversation_id, contact_detail_id, operator_id, surface)
            values (@id, @occurredAt, @siteId, @conversationId, @contactDetailId, @operatorId, @surface)
            """,
            connection);
        command.Parameters.AddWithValue("id", reveal.Id);
        command.Parameters.AddWithValue("occurredAt", reveal.OccurredAt);
        command.Parameters.AddWithValue("siteId", reveal.SiteId.Value);
        command.Parameters.AddWithValue("conversationId", reveal.ConversationId);
        command.Parameters.AddWithValue("contactDetailId", reveal.ContactDetailId);
        command.Parameters.AddWithValue("operatorId", reveal.OperatorId.Value);
        command.Parameters.AddWithValue("surface", reveal.Surface);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ContactRevealPage> ListForSiteAsync(
        SiteId siteId, Guid? beforeId, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select id, occurred_at, conversation_id, contact_detail_id, operator_id, surface
            from contact_reveals
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
        // One extra row, not returned - the same "ask for limit+1" shape AccessRecordRepository's own
        // keyset read uses, so paging needs no separate count query.
        command.Parameters.AddWithValue("limit", limit + 1);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<ContactRevealItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ContactRevealItem(
                reader.GetGuid(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                reader.GetGuid(2),
                reader.GetGuid(3),
                reader.GetGuid(4),
                reader.GetString(5)));
        }

        var hasMore = items.Count > limit;
        if (hasMore)
        {
            items.RemoveRange(limit, items.Count - limit);
        }

        var nextBeforeId = hasMore ? items[^1].Id : (Guid?)null;

        return new ContactRevealPage(items, nextBeforeId);
    }
}
