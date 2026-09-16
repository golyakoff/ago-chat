using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `23-78`: raw SQL, not EF's usual LINQ surface - <see cref="IConversationAttachmentUploadGrantRepository"/>'s
/// own remarks explain why this deliberately bypasses <see cref="Conversation"/>'s usual aggregate
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
///
/// <para><b>`25-110`: through the shared <see cref="AgoChatDbContext"/> connection now, not a private
/// <c>NpgsqlDataSource</c>.</b> This was the raw-Npgsql-over-its-own-connection shape
/// <see cref="ConversationBlockRepository"/> still uses, until `25-110` needed
/// <see cref="GrantAsync"/>/<see cref="RevokeAsync"/> to commit atomically with an outbox row
/// (<c>GrantAttachmentUploadHandler</c>'s own remarks: CLAUDE.md rule 4 forbids the alternative, an
/// outbox write on a separate connection with no shared transaction). A private
/// <c>NpgsqlDataSource.OpenConnectionAsync</c> call opens a *different* physical connection than the one
/// <c>AgoChatDbContext</c>/<see cref="Application.Abstractions.IUnitOfWork"/> hold open for the same
/// request, so a caller-owned transaction begun on the latter would never cover a statement issued on
/// the former - two connections cannot share one Postgres transaction. Reworked onto the identical
/// "build an <see cref="NpgsqlCommand"/> against <c>AgoChatDbContext</c>'s own connection and its current
/// transaction, when one is open" shape <c>ConversationAttachmentBudgetStore</c> already established for
/// the same reason (that type's own remarks) - a plain <c>Database.ExecuteSqlInterpolatedAsync</c> cannot
/// be used instead because this needs a result set back (the exists/updated pair), which that helper
/// cannot give.</para>
/// </summary>
public sealed class ConversationAttachmentUploadGrantRepository(AgoChatDbContext db)
    : IConversationAttachmentUploadGrantRepository
{
    private const string GrantSql = """
        with updated as (
            update conversations
            set attachment_upload_granted_at = @now, attachment_upload_granted_by = @actorId
            where id = @conversationId and site_id = @siteId and attachment_upload_granted_at is null
            returning id
        )
        select
            exists (select 1 from conversations where id = @conversationId and site_id = @siteId) as "Exists",
            exists (select 1 from updated) as "Updated"
        """;

    private const string RevokeSql = """
        with updated as (
            update conversations
            set attachment_upload_granted_at = null, attachment_upload_granted_by = null
            where id = @conversationId and site_id = @siteId and attachment_upload_granted_at is not null
            returning id
        )
        select
            exists (select 1 from conversations where id = @conversationId and site_id = @siteId) as "Exists",
            exists (select 1 from updated) as "Updated"
        """;

    public Task<AttachmentUploadGrantOutcome> GrantAsync(
        ConversationId conversationId, SiteId siteId, OperatorId grantedBy, DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ExecuteAsync(conversationId, siteId, grantedBy, now, GrantSql, cancellationToken);

    public Task<AttachmentUploadGrantOutcome> RevokeAsync(
        ConversationId conversationId, SiteId siteId, OperatorId revokedBy, DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ExecuteAsync(conversationId, siteId, revokedBy, now, RevokeSql, cancellationToken);

    private async Task<AttachmentUploadGrantOutcome> ExecuteAsync(
        ConversationId conversationId, SiteId siteId, OperatorId actorId, DateTimeOffset now, string sql,
        CancellationToken cancellationToken)
    {
        await using var command = await CreateCommandAsync(sql, cancellationToken);
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

    /// <summary>Opens the connection if this is the first statement issued through it this request - a
    /// caller-owned transaction (<c>IUnitOfWork.BeginTransactionAsync</c>) already opens it in
    /// production, but a standalone caller (this class's own repository-level tests) has not, and this
    /// needs a result set back, which <c>Database.ExecuteSqlInterpolatedAsync</c> cannot give - the
    /// identical reasoning and the identical shape <c>ConversationAttachmentBudgetStore.CreateCommandAsync</c>
    /// already established. <c>Database.OpenConnectionAsync</c>, not the raw ADO connection's own
    /// <c>OpenAsync</c>: it participates in EF's own connection reference count, so it is safe to call
    /// even when a transaction already opened the connection.</summary>
    private async Task<NpgsqlCommand> CreateCommandAsync(string sql, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
        return new NpgsqlCommand(sql, connection, transaction);
    }
}
