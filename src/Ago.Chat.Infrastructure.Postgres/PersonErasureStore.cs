using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `adr/0189`/`26-275` slice #3: raw Npgsql against <c>visitors.erasure_requested_at</c> - the identical
/// "reaches a row without going through its aggregate's usual load-mutate-save" shape
/// <see cref="ErasureRequestRepository"/> already establishes for <c>sites</c>/<c>conversations</c>,
/// applied here to a third table for the same reason: <see cref="VisitorConfiguration"/>'s own shadow
/// property is written only here and read only by <c>PersonErasureJob</c> (<c>Ago.Chat.Worker</c>), never
/// through <see cref="Visitor"/>'s own aggregate.
///
/// <para><b>One statement, `WHERE ... IS NULL`, no receipt.</b> Unlike <see cref="ErasureRequestRepository"/>'s
/// own pair, there is no <c>erasure_records</c> row to insert alongside the flag - see
/// <see cref="IPersonErasureStore"/>'s own remarks for why a system-to-system cascade mints no receipt.
/// That removes the reason those two methods need a data-modifying CTE (keeping two writes atomic): this
/// is a single conditional <c>UPDATE</c>, and the only race it has to be safe under is two deliveries of
/// the same <c>PersonErased</c> event landing concurrently - the `WHERE erasure_requested_at IS NULL`
/// predicate is what makes exactly one of them the one that reports <see cref="PersonErasureOutcome.Requested"/>,
/// the same read-committed row-lock argument <see cref="ErasureRequestRepository"/>'s own remarks give in
/// full.</para>
/// </summary>
public sealed class PersonErasureStore(NpgsqlDataSource dataSource) : IPersonErasureStore
{
    public async Task<PersonErasureOutcome> RequestErasureIfPresentAsync(
        SiteId accountId, VisitorId personId, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        await using (var update = new NpgsqlCommand(
            """
            update visitors
            set erasure_requested_at = @occurredAt
            where id = @personId and site_id = @accountId and erasure_requested_at is null
            returning id
            """,
            connection))
        {
            update.Parameters.AddWithValue("occurredAt", occurredAt);
            update.Parameters.AddWithValue("personId", personId.Value);
            update.Parameters.AddWithValue("accountId", accountId.Value);

            var stamped = await update.ExecuteScalarAsync(cancellationToken);
            if (stamped is not null)
            {
                return PersonErasureOutcome.Requested;
            }
        }

        // Either already flagged (a redelivery, or a sweep already under way) or this account holds no
        // such person at all - the second read IPersonErasureStore's own remarks describe: not a second
        // write, so no atomicity is needed with the UPDATE above, only an honest answer for the consumer
        // to log by.
        await using var exists = new NpgsqlCommand(
            "select exists(select 1 from visitors where id = @personId and site_id = @accountId)", connection);
        exists.Parameters.AddWithValue("personId", personId.Value);
        exists.Parameters.AddWithValue("accountId", accountId.Value);
        var found = (bool)(await exists.ExecuteScalarAsync(cancellationToken))!;
        return found ? PersonErasureOutcome.AlreadyRequested : PersonErasureOutcome.UnknownPerson;
    }
}
