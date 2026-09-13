using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `23-76`'s <see cref="ISiteAttachmentStorageBudget"/> - the identical statement shape
/// <see cref="ConversationAttachmentBudgetStore"/> already established for the conversation-level
/// budget (see that class's own remarks for the full "one locked read, not two" reasoning this mirrors
/// verbatim), against `sites.attachment_bytes_reserved` instead of
/// `conversations.attachment_bytes_reserved`. Two adapters for the identical CTE rather than one
/// parameterized by table name: this codebase has no precedent for a raw-SQL adapter that takes its
/// own table name as a runtime parameter, and doing so here would trade a few duplicated lines for a
/// string-built SQL statement - the worse trade (`ConversationAttachmentBudgetStore` itself is ~15
/// lines of SQL; the risk of a table-name injection bug from ever mis-wiring that parameter outweighs
/// the duplication it would save).
/// </summary>
public sealed class SiteAttachmentStorageBudgetStore(AgoChatDbContext db) : ISiteAttachmentStorageBudget
{
    private const string ReserveSql = """
        WITH current AS (
            SELECT
                attachment_bytes_reserved AS before,
                CASE WHEN attachment_bytes_reserved + @bytes <= @budgetBytes THEN @bytes ELSE 0 END AS increment
            FROM sites
            WHERE id = @siteId
            FOR UPDATE
        ),
        updated AS (
            UPDATE sites
            SET attachment_bytes_reserved = attachment_bytes_reserved + (SELECT increment FROM current)
            WHERE id = @siteId
            RETURNING attachment_bytes_reserved
        )
        SELECT updated.attachment_bytes_reserved, current.increment > 0
        FROM updated, current
        """;

    public async Task<AttachmentBudgetResult> TryReserveAsync(
        SiteId siteId, long bytes, long budgetBytes, CancellationToken cancellationToken)
    {
        await using var command = await CreateCommandAsync(ReserveSql, cancellationToken);
        command.Parameters.AddWithValue("siteId", siteId.Value);
        command.Parameters.AddWithValue("bytes", bytes);
        command.Parameters.AddWithValue("budgetBytes", budgetBytes);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            // AttachmentConfiguration.HasOne<Site>'s foreign key makes this unreachable in production -
            // CreateAttachmentHandler already loaded this exact site by id before ever reaching this call.
            throw new InvalidOperationException(
                $"Site {siteId.Value} was not found while reserving its attachment storage budget.");
        }

        var reservedTotal = reader.GetInt64(0);
        var reserved = reader.GetBoolean(1);
        // Floored at zero: the same "never report a nonsensical negative" guard
        // ConversationAttachmentBudgetStore's own TryReserveAsync applies, for the identical reason -
        // a tier downgrade lowering the ceiling below what is already reserved must not go negative.
        return new AttachmentBudgetResult(reserved, Math.Max(budgetBytes - reservedTotal, 0));
    }

    public async Task ReleaseAsync(SiteId siteId, long bytes, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE sites
            SET attachment_bytes_reserved = GREATEST(attachment_bytes_reserved - {bytes}, 0)
            WHERE id = {siteId.Value}
            """,
            cancellationToken);
    }

    /// <summary>See <see cref="ConversationAttachmentBudgetStore"/>'s own identical helper - same
    /// reasoning, same "open the connection if this is the first statement issued through it, and
    /// build the command against the ambient connection/transaction directly since a result set is
    /// needed" contract.</summary>
    private async Task<NpgsqlCommand> CreateCommandAsync(string sql, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
        return new NpgsqlCommand(sql, connection, transaction);
    }
}
