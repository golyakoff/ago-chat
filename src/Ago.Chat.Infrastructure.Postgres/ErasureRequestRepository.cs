using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `16-02`: raw Npgsql, not EF - <see cref="IErasureRequestRepository"/>'s own remarks explain why
/// this deliberately bypasses <see cref="Site"/>/<see cref="Conversation"/>'s usual aggregate
/// load-mutate-save, the same "reaches a row without going through its aggregate" shape
/// <see cref="DemoTenantRepository"/> already established.
/// </summary>
public sealed class ErasureRequestRepository(NpgsqlDataSource dataSource) : IErasureRequestRepository
{
    public async Task<bool> RequestSiteErasureAsync(
        SiteId siteId, DateTimeOffset requestedAt, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        // coalesce(...): idempotent on a repeat call - the second (or Nth) request for the same site
        // preserves the original request time rather than pushing it forward, which matters because
        // `15-02`'s 30-day backup-retention completeness claim is measured from when erasure was first
        // requested, not from whichever request happened to be the caller's own.
        await using var command = new NpgsqlCommand(
            "update sites set erasure_requested_at = coalesce(erasure_requested_at, @requestedAt) where id = @siteId",
            connection);
        command.Parameters.AddWithValue("requestedAt", requestedAt);
        command.Parameters.AddWithValue("siteId", siteId.Value);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    public async Task<bool> RequestConversationErasureAsync(
        ConversationId conversationId, SiteId siteId, DateTimeOffset requestedAt, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            update conversations
            set erasure_requested_at = coalesce(erasure_requested_at, @requestedAt)
            where id = @conversationId and site_id = @siteId
            """,
            connection);
        command.Parameters.AddWithValue("requestedAt", requestedAt);
        command.Parameters.AddWithValue("conversationId", conversationId.Value);
        command.Parameters.AddWithValue("siteId", siteId.Value);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }
}
