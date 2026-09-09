using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `23-78`: raw Npgsql, not EF - <see cref="IConversationAttachmentUploadGrantRepository"/>'s own
/// remarks explain why this deliberately bypasses <see cref="Conversation"/>'s usual aggregate
/// load-mutate-save, the same shape <see cref="ConversationBlockRepository"/> already established for
/// the sibling current-state pair on this same table.
///
/// <para><b>One statement, not two - and one fewer than <see cref="ConversationBlockRepository"/>'s
/// own two.</b> Each method below is a single SQL statement built from one data-modifying CTE (the
/// conditional <c>UPDATE ... WHERE attachment_upload_granted_at IS [NOT] NULL</c> that both makes two
/// concurrent requests race-free and tells this method whether anything actually changed) plus a bare
/// existence check, with no second CTE inserting an audit-trail row -
/// <see cref="IConversationAttachmentUploadGrantRepository"/>'s own remarks state why this item builds
/// no <c>conversation_block_records</c>-shaped table for itself.</para>
/// </summary>
public sealed class ConversationAttachmentUploadGrantRepository(NpgsqlDataSource dataSource)
    : IConversationAttachmentUploadGrantRepository
{
    public Task<AttachmentUploadGrantOutcome> GrantAsync(
        ConversationId conversationId, SiteId siteId, OperatorId grantedBy, DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            conversationId, siteId, grantedBy, now,
            """
            with updated as (
                update conversations
                set attachment_upload_granted_at = @now, attachment_upload_granted_by = @actorId
                where id = @conversationId and site_id = @siteId and attachment_upload_granted_at is null
                returning id
            )
            select
                exists (select 1 from conversations where id = @conversationId and site_id = @siteId) as "Exists",
                exists (select 1 from updated) as "Updated"
            """,
            cancellationToken);

    public Task<AttachmentUploadGrantOutcome> RevokeAsync(
        ConversationId conversationId, SiteId siteId, OperatorId revokedBy, DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            conversationId, siteId, revokedBy, now,
            """
            with updated as (
                update conversations
                set attachment_upload_granted_at = null, attachment_upload_granted_by = null
                where id = @conversationId and site_id = @siteId and attachment_upload_granted_at is not null
                returning id
            )
            select
                exists (select 1 from conversations where id = @conversationId and site_id = @siteId) as "Exists",
                exists (select 1 from updated) as "Updated"
            """,
            cancellationToken);

    private async Task<AttachmentUploadGrantOutcome> ExecuteAsync(
        ConversationId conversationId, SiteId siteId, OperatorId actorId, DateTimeOffset now, string sql,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("conversationId", conversationId.Value);
        command.Parameters.AddWithValue("siteId", siteId.Value);
        command.Parameters.AddWithValue("actorId", actorId.Value);
        command.Parameters.AddWithValue("now", now);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var exists = reader.GetBoolean(0);
        var updated = reader.GetBoolean(1);

        if (!exists)
        {
            return AttachmentUploadGrantOutcome.NotFound;
        }

        return updated ? AttachmentUploadGrantOutcome.Applied : AttachmentUploadGrantOutcome.AlreadyInState;
    }
}
