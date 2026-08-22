using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `4-01`'s `IOperatorCapacity` - raw Npgsql, not EF, for the same reason
/// <c>ConversationReadStore</c> already is (`adr/0004`): the atomic compare-and-set
/// `concurrency.md` specifies has no LINQ shape, and this store never needs change tracking. The
/// only writer of `operators.active_chats` - <c>OperatorConfiguration</c>'s shadow property exists
/// so EF knows the column for migrations, but SaveChanges never touches it.
/// </summary>
public sealed class OperatorCapacityStore(NpgsqlDataSource dataSource) : IOperatorCapacity
{
    public async Task<bool> TryClaimAsync(OperatorId operatorId, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE operators
            SET active_chats = active_chats + 1
            WHERE id = @id AND active_chats < capacity
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", operatorId.Value);

        var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
        return rowsAffected > 0;
    }

    public async Task ReleaseAsync(OperatorId operatorId, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE operators
            SET active_chats = active_chats - 1
            WHERE id = @id AND active_chats > 0
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", operatorId.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
