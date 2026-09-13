using Ago.Chat.Application.Abstractions;
using Dapper;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `25-73`: a single indexed existence check - `email = @Email` has no dedicated index of its own
/// today (only `code_hash` and `site_id` do), so this is a sequential scan over `operator_invites`;
/// left unindexed deliberately for now, since this table is small (`13-01`'s own "at most a handful of
/// calls ever per site") and this read runs once per `/onboarding` mount, not on any hot path - flagged
/// here rather than silently assumed fast, per `CLAUDE.md`'s "measure or stay silent."
/// </summary>
public sealed class PendingOperatorInviteByEmailReadStore(NpgsqlDataSource dataSource) : IPendingOperatorInviteByEmailReadStore
{
    private const string Sql = """
        select exists(
            select 1 from operator_invites
            where email = @Email and redeemed_at is null and revoked_at is null and expires_at > @Now
        )
        """;

    public async Task<bool> AnyPendingForEmailAsync(string email, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            Sql, new { Email = email, Now = now }, cancellationToken: cancellationToken));
    }
}
