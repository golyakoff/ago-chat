using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `24-10`: raw Npgsql, not EF - <see cref="IConversationBlockRepository"/>'s own remarks explain why
/// this deliberately bypasses <see cref="Conversation"/>'s usual aggregate load-mutate-save, the same
/// shape <see cref="ErasureRequestRepository"/> already established for the sibling shadow-property
/// pair on this same table.
///
/// <para><b>One statement, not two.</b> Each method below is a single SQL statement built from
/// data-modifying CTEs: the first CTE does the conditional <c>UPDATE ... WHERE blocked_at IS [NOT]
/// NULL</c> that both makes two concurrent requests race-free (only one can see the row in the state it
/// is looking for and win it) and tells this method whether anything actually changed; the second CTE
/// inserts the matching <c>conversation_block_records</c> row only when the first one actually updated a
/// row. A final bare <c>SELECT</c> answers whether the conversation exists at all, independent of
/// whether the update fired - the three-way <see cref="ConversationBlockOutcome"/> a caller needs is
/// computed from those two booleans, never from a second round trip.</para>
/// </summary>
public sealed class ConversationBlockRepository(NpgsqlDataSource dataSource) : IConversationBlockRepository
{
    public Task<ConversationBlockOutcome> BlockAsync(
        ConversationId conversationId, SiteId siteId, OperatorId blockedBy, Guid blockRecordId, DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            conversationId, siteId, blockedBy, blockRecordId, now, ConversationBlockRecordKind.Blocked,
            """
            with updated as (
                update conversations
                set blocked_at = @now, blocked_by = @actorId
                where id = @conversationId and site_id = @siteId and blocked_at is null
                returning id
            ),
            inserted as (
                insert into conversation_block_records (id, conversation_id, site_id, kind, actor_id, occurred_at)
                select @recordId, @conversationId, @siteId, @kind, @actorId, @now
                where exists (select 1 from updated)
            )
            select
                exists (select 1 from conversations where id = @conversationId and site_id = @siteId) as "Exists",
                exists (select 1 from updated) as "Updated"
            """,
            cancellationToken);

    public Task<ConversationBlockOutcome> UnblockAsync(
        ConversationId conversationId, SiteId siteId, OperatorId unblockedBy, Guid blockRecordId, DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            conversationId, siteId, unblockedBy, blockRecordId, now, ConversationBlockRecordKind.Unblocked,
            """
            with updated as (
                update conversations
                set blocked_at = null, blocked_by = null
                where id = @conversationId and site_id = @siteId and blocked_at is not null
                returning id
            ),
            inserted as (
                insert into conversation_block_records (id, conversation_id, site_id, kind, actor_id, occurred_at)
                select @recordId, @conversationId, @siteId, @kind, @actorId, @now
                where exists (select 1 from updated)
            )
            select
                exists (select 1 from conversations where id = @conversationId and site_id = @siteId) as "Exists",
                exists (select 1 from updated) as "Updated"
            """,
            cancellationToken);

    private async Task<ConversationBlockOutcome> ExecuteAsync(
        ConversationId conversationId, SiteId siteId, OperatorId actorId, Guid recordId, DateTimeOffset now,
        ConversationBlockRecordKind kind, string sql, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("conversationId", conversationId.Value);
        command.Parameters.AddWithValue("siteId", siteId.Value);
        command.Parameters.AddWithValue("actorId", actorId.Value);
        command.Parameters.AddWithValue("recordId", recordId);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("kind", kind.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var exists = reader.GetBoolean(0);
        var updated = reader.GetBoolean(1);

        if (!exists)
        {
            return ConversationBlockOutcome.NotFound;
        }

        return updated ? ConversationBlockOutcome.Applied : ConversationBlockOutcome.AlreadyInState;
    }
}
