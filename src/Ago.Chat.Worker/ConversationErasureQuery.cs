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
/// <summary>`15-09`/`adr/0087`: one pending conversation, with the `site_id` its own erasure needs to
/// scope <see cref="ConversationErasureQuery.DeleteMessageBatchAsync"/> against - `messages` is now
/// `PARTITION BY HASH (site_id)`, so a delete keyed on `conversation_id` alone would still have to probe
/// all 64 buckets to find the rows before restricting to one conversation's worth.</summary>
public sealed record PendingConversationErasure(Guid ConversationId, Guid SiteId);

public static class ConversationErasureQuery
{
    public static async Task<IReadOnlyList<PendingConversationErasure>> ListPendingAsync(
        NpgsqlConnection connection, int limit, CancellationToken cancellationToken)
    {
        const string sql = """
            select id, site_id
            from conversations
            where erasure_requested_at is not null
            order by erasure_requested_at
            limit @limit
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("limit", limit);

        var pending = new List<PendingConversationErasure>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            pending.Add(new PendingConversationErasure(reader.GetGuid(0), reader.GetGuid(1)));
        }

        return pending;
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
    /// sequential one.
    ///
    /// <para>`15-09`/`adr/0087`: <paramref name="siteId"/> is new - `messages` is now `PARTITION BY
    /// HASH (site_id)`, so both the inner and outer statement's own `site_id = @siteId` are what let
    /// Postgres prune to the one bucket this conversation's messages live in, instead of probing all 64
    /// to find rows a `conversation_id`-only predicate would still locate correctly but slowly. The
    /// caller (`ConversationErasureJob`) already has it from `ListPendingAsync`'s own
    /// <see cref="PendingConversationErasure"/>.</para></summary>
    public static async Task<int> DeleteMessageBatchAsync(
        NpgsqlConnection connection, Guid conversationId, Guid siteId, int batchSize, CancellationToken cancellationToken)
    {
        const string sql = """
            delete from messages
            where site_id = @siteId
              and id in (
                select id
                from messages
                where conversation_id = @conversationId
                  and site_id = @siteId
                order by sequence
                limit @batchSize
                for update skip locked
            )
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("conversationId", conversationId);
        command.Parameters.AddWithValue("siteId", siteId);
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

    /// <summary>
    /// `18-04`/`16-02`: a note is personal data about a visitor, written by an operator
    /// (<c>ConversationNote</c>'s own remarks) - in scope for erasure the same as every message.
    /// Bounded the same way <see cref="DeleteAttachmentsAsync"/> is (small relative to a
    /// conversation's own message count, an operator writes at most a handful of notes per
    /// conversation), so this is one unbounded statement, not a batched loop. Never reached through
    /// <c>INoteRepository</c> - this Worker job talks to Postgres directly, the same "raw Npgsql,
    /// forward-only" shape every other method in this file uses, and <c>INoteRepository</c>'s own
    /// remarks are explicit that it has exactly two real callers, neither of them this one.
    /// </summary>
    public static async Task<int> DeleteNotesForConversationAsync(
        NpgsqlConnection connection, Guid conversationId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "delete from conversation_notes where conversation_id = @conversationId", connection);
        command.Parameters.AddWithValue("conversationId", conversationId);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// `18-04`/`16-02`: this conversation's own tag *associations* - the tag *definitions* (`tags`)
    /// are untouched, since another conversation on the same site may still carry them; only the rows
    /// naming this specific conversation disappear. The same "small, one unbounded statement" shape as
    /// <see cref="DeleteNotesForConversationAsync"/> right above.
    /// </summary>
    public static async Task<int> DeleteTagsForConversationAsync(
        NpgsqlConnection connection, Guid conversationId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "delete from conversation_tags where conversation_id = @conversationId", connection);
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
