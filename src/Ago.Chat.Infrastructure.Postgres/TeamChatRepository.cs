using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `23-32`'s <see cref="ITeamChatRepository"/> adapter - one implicit transaction per
/// <see cref="PostAsync"/> call, the same shape <c>ModuleQuantityGrantStore.GrantAsync</c> already
/// uses: the message row and the outbox row it stages both ride the one <c>SaveChangesAsync</c> below,
/// so they commit or roll back together (CLAUDE.md rule 4).
///
/// <para><b>The sequence assignment is a separate, already-committed statement, not part of that same
/// transaction - and that is deliberate, not an oversight.</b> <see cref="NextSequenceAsync"/> runs
/// over its own connection (<see cref="NpgsqlDataSource"/>, the same per-call-connection shape
/// <c>OperatorTeamReadStore</c> already uses) and commits the instant it returns. If the later
/// <c>SaveChangesAsync</c> then fails (a genuine <c>clientMessageId</c> race is the only case this
/// repository catches), the sequence value that attempt claimed is never
/// reused - a small permanent gap in an otherwise-monotonic counter, the identical behaviour any
/// Postgres <c>SERIAL</c>/<c>IDENTITY</c> column already exhibits across a rolled-back transaction.
/// That gap is harmless here specifically because nothing about this feature needs gapless numbering
/// (contrast <see cref="IOperatorCapacity.ClaimAsync"/>, which genuinely must share its caller's own
/// transaction: a claim that is not rolled back with the assignment it was for is a leaked capacity
/// slot, not a cosmetic gap) - so paying for a second connection to join one ambient transaction here
/// would buy nothing this feature needs.</para>
/// </summary>
public sealed class TeamChatRepository(
    AgoChatDbContext db, NpgsqlDataSource dataSource, IOutboxWriter outbox, IIdGenerator idGenerator)
    : ITeamChatRepository
{
    public async Task<TeamMessage> PostAsync(
        SiteId siteId,
        OperatorId authorId,
        bool authorIsAdmin,
        MessageBody body,
        Guid? clientMessageId,
        TeamMessageId id,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var sequence = await NextSequenceAsync(siteId, cancellationToken);
        var message = new TeamMessage(id, siteId, authorId, authorIsAdmin, body, sequence, clientMessageId, now);

        db.TeamMessages.Add(message);
        outbox.Enqueue(TeamMessagePostedMapper.ToEnvelope(id.Value, siteId.Value, sequence, now, idGenerator));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (clientMessageId is not null && ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "ix_team_messages_site_client_message_id",
        })
        {
            // `5-07`'s retry-dedup: the same clientMessageId already produced a real message (a
            // caller retrying after a SendOutcomeUnknownError-shaped failure) - return that one
            // instead of a second post, or a raw constraint-violation error the caller cannot act on.
            db.ChangeTracker.Clear();
            var existing = await db.TeamMessages.AsNoTracking()
                .SingleAsync(m => m.SiteId == siteId && m.ClientMessageId == clientMessageId, cancellationToken);
            return existing;
        }

        return message;
    }

    /// <summary>The atomic compare-and-set CLAUDE.md rule 8 asks for: assigned inside the database,
    /// never computed from a value this process already holds. See this type's own remarks for why it
    /// runs over its own connection rather than participating in <see cref="PostAsync"/>'s later
    /// <c>SaveChangesAsync</c>.</summary>
    private async Task<int> NextSequenceAsync(SiteId siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "update sites set team_chat_last_sequence = team_chat_last_sequence + 1 where id = @siteId returning team_chat_last_sequence",
            connection);
        command.Parameters.AddWithValue("siteId", siteId.Value);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return (int)result!;
    }
}
