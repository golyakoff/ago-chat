using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `16-02`: raw Npgsql, not EF - <see cref="IErasureRequestRepository"/>'s own remarks explain why
/// this deliberately bypasses <see cref="Site"/>/<see cref="Conversation"/>'s usual aggregate
/// load-mutate-save, the same "reaches a row without going through its aggregate" shape
/// <see cref="DemoTenantRepository"/> already established.
///
/// <para><b>`24-13`: one statement, not two.</b> Each method below is a single SQL statement built
/// from data-modifying CTEs: the first CTE does the exact same conditional `UPDATE ... WHERE
/// erasure_requested_at IS NULL` this repository always did (the row lock that condition takes is
/// what makes two concurrent requests for the same site/conversation race-free under Postgres's own
/// read-committed rules - only one can see `erasure_requested_at IS NULL` and win it), and the second
/// CTE inserts the matching <c>erasure_records</c> row **only when the first one actually updated a
/// row** (`WHERE EXISTS (SELECT 1 FROM stamped)`). Because both live in one statement, Postgres runs
/// them as one atomic unit - there is no window in which the flag is set but the receipt row is not,
/// and no explicit `BEGIN`/`COMMIT` is needed to get that guarantee. Splitting this into "stamp the
/// flag" then "insert the receipt" as two round trips would reopen exactly that window: a crash
/// between them would leave a site or conversation flagged for erasure with no <c>erasure_records</c>
/// row for the job to ever complete or fail - the receipt <see cref="IErasureRequestRepository"/>'s
/// own remarks promise would then not exist for that request.</para>
/// </summary>
public sealed class ErasureRequestRepository(NpgsqlDataSource dataSource) : IErasureRequestRepository
{
    public async Task<bool> RequestSiteErasureAsync(
        SiteId siteId, OperatorId requestedBy, Guid erasureRecordId, DateTimeOffset requestedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            with stamped as (
                update sites
                set erasure_requested_at = @requestedAt,
                    erasure_requested_by = @requestedBy,
                    erasure_record_id = @recordId
                where id = @siteId and erasure_requested_at is null
                returning id
            ),
            inserted as (
                insert into erasure_records (id, scope, site_id, requested_by, status, requested_at)
                select @recordId, 'Site', @siteId, @requestedBy, 'Pending', @requestedAt
                where exists (select 1 from stamped)
            )
            select exists (select 1 from sites where id = @siteId)
            """,
            connection);
        command.Parameters.AddWithValue("siteId", siteId.Value);
        command.Parameters.AddWithValue("requestedBy", requestedBy.Value);
        command.Parameters.AddWithValue("recordId", erasureRecordId);
        command.Parameters.AddWithValue("requestedAt", requestedAt);

        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<bool> RequestConversationErasureAsync(
        ConversationId conversationId, SiteId siteId, OperatorId requestedBy, Guid erasureRecordId,
        DateTimeOffset requestedAt, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            with stamped as (
                update conversations
                set erasure_requested_at = @requestedAt,
                    erasure_requested_by = @requestedBy,
                    erasure_record_id = @recordId
                where id = @conversationId and site_id = @siteId and erasure_requested_at is null
                returning id
            ),
            inserted as (
                insert into erasure_records (id, scope, site_id, requested_by, status, requested_at)
                select @recordId, 'Conversation', @siteId, @requestedBy, 'Pending', @requestedAt
                where exists (select 1 from stamped)
            )
            select exists (
                select 1 from conversations where id = @conversationId and site_id = @siteId
            )
            """,
            connection);
        command.Parameters.AddWithValue("conversationId", conversationId.Value);
        command.Parameters.AddWithValue("siteId", siteId.Value);
        command.Parameters.AddWithValue("requestedBy", requestedBy.Value);
        command.Parameters.AddWithValue("recordId", erasureRecordId);
        command.Parameters.AddWithValue("requestedAt", requestedAt);

        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
