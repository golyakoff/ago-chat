using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `16-02`: the SQL half of <see cref="ConversationErasureJob"/> - raw Npgsql, the same
/// raw-SQL-in-the-Worker shape <see cref="OutboxPruneQuery"/>/<see cref="AttachmentOrphanSweepQuery"/>
/// already establish, kept as one file because every method here is one step of the same ordered
/// removal sequence rather than an independent query.
///
/// <para><b>Why the outer claim (<see cref="ListPendingAsync"/>) is a plain read, not a
/// <c>FOR UPDATE SKIP LOCKED</c> claim like the other two.</b> Those two delete-and-return in one
/// statement, so the locked window is a single instant. A conversation's erasure is a multi-step
/// sequence that reaches MinIO in between (<see cref="ConversationErasureJob"/>'s own ordering) -
/// holding a row lock open across external I/O for the whole sequence is exactly the anti-pattern the
/// single-statement shape exists to avoid. Instead this follows <c>DemoTenantExpiryJob</c>'s own
/// precedent for the identical kind of multi-step, external-I/O-heavy removal: a plain bounded read
/// picks candidates, and every step of the removal is naturally idempotent (deleting an
/// already-deleted object, row, or Keycloak user is a defined no-op per each port's own contract), so
/// two replicas racing the same conversation do redundant work at worst, never corrupt or duplicate
/// state.</b> <see cref="DeleteMessageBatchAsync"/> is the one place <c>FOR UPDATE SKIP LOCKED</c>
/// still appears, because it *is* the single-statement, no-I/O-in-between shape.</para>
/// </summary>
public static class ConversationErasureQuery
{
    public static async Task<IReadOnlyList<Guid>> ListPendingAsync(
        NpgsqlConnection connection, int limit, CancellationToken cancellationToken)
    {
        const string sql = """
            select id
            from conversations
            where erasure_requested_at is not null
            order by erasure_requested_at
            limit @limit
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("limit", limit);

        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    /// <summary>Every attachment object key and thumbnail key for this conversation - read before any
    /// row is deleted, the same "object store first" ordering <c>DemoTenantExpiryJob</c>'s own remarks
    /// give: after the attachment rows are gone there is nothing left to enumerate, and the bytes
    /// would orphan in MinIO forever.</summary>
    public static async Task<IReadOnlyList<string>> ListAttachmentObjectKeysAsync(
        NpgsqlConnection connection, Guid conversationId, CancellationToken cancellationToken)
    {
        const string sql = """
            select object_key from attachments where conversation_id = @conversationId
            union all
            select thumbnail_key from attachments where conversation_id = @conversationId and thumbnail_key is not null
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("conversationId", conversationId);

        var keys = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(0))
            {
                keys.Add(reader.GetString(0));
            }
        }

        return keys;
    }

    /// <summary>Bounded-batch delete, `messages`' own partitioned-table version of
    /// <see cref="OutboxPruneQuery.DeletePublishedBatchAsync"/> - one conversation's history can be
    /// large (`16-02`'s own instruction), so this is looped by the caller rather than issued as one
    /// unbounded `DELETE ... WHERE conversation_id = @id`. Ordered by `sequence`, the natural order
    /// within a conversation and the leading non-`conversation_id` column of the covering unique index
    /// `MessageConfiguration` already declares, so the subquery is an index scan rather than a
    /// sequential one.</summary>
    public static async Task<int> DeleteMessageBatchAsync(
        NpgsqlConnection connection, Guid conversationId, int batchSize, CancellationToken cancellationToken)
    {
        const string sql = """
            delete from messages
            where id in (
                select id
                from messages
                where conversation_id = @conversationId
                order by sequence
                limit @batchSize
                for update skip locked
            )
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("conversationId", conversationId);
        command.Parameters.AddWithValue("batchSize", batchSize);

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The attachment rows themselves, once every object they named is gone and every
    /// message that could reference one is gone - bounded by "how many attachments one conversation
    /// has", which is small relative to its message count, so this is one statement rather than a
    /// batched loop like <see cref="DeleteMessageBatchAsync"/>.</summary>
    public static async Task<int> DeleteAttachmentsAsync(
        NpgsqlConnection connection, Guid conversationId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "delete from attachments where conversation_id = @conversationId", connection);
        command.Parameters.AddWithValue("conversationId", conversationId);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The conversation row itself - last, once every row and object it owns is confirmed
    /// gone. A stray message or attachment this sequence somehow missed would still cascade-delete
    /// with it (`ConversationConfiguration`'s own `OnDelete(DeleteBehavior.Cascade)` on `_messages`;
    /// `AttachmentConfiguration`'s required FK to `Conversation` defaults to the same), which is
    /// defence in depth, not the primary mechanism - the primary mechanism is the bounded, ordered
    /// deletion above, precisely so a real tenant's large history is never removed via one unbounded
    /// cascading `DELETE`.</summary>
    public static async Task<int> DeleteConversationAsync(
        NpgsqlConnection connection, Guid conversationId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "delete from conversations where id = @conversationId", connection);
        command.Parameters.AddWithValue("conversationId", conversationId);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
