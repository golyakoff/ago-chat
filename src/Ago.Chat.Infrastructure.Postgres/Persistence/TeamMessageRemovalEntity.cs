using Ago.Chat.Domain;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `23-33`: exists solely so `dotnet ef migrations add` can generate `team_message_removals`' own
/// `CREATE TABLE` from a declarative model - the identical "migration-scaffolding only, nothing ever
/// queries this DbSet directly" shape <see cref="ModuleRevokeOverrideEntity"/>'s own remarks give in
/// full (`db-migration` skill). Unlike that type, this one *is* written through EF
/// (<c>TeamChatRepository.RemoveAsync</c> adds it to the same change-tracked <c>SaveChangesAsync</c>
/// that persists the tombstoned <see cref="TeamMessage"/> row and stages the outbox row) rather than
/// raw Npgsql - CLAUDE.md rule 4 requires the state change and its integration event to commit
/// together, and unlike `module_revoke_overrides` (a synchronous audit write with no event of its
/// own, `adr/0118`), this table's own write must be atomic with a real `Ago.Chat.Contracts.TeamMessageRemoved`
/// outbox row, so it rides the transaction EF already owns rather than opening a second one raw Npgsql
/// would need to coordinate separately.
///
/// <para><b>A real foreign key to <c>sites</c>, deliberately diverging from
/// <see cref="ModuleRevokeOverrideEntity"/>'s own no-FK choice - see
/// <see cref="TeamMessageRemovalEntityConfiguration"/>'s own remarks for the full reasoning.</b></para>
/// </summary>
internal sealed class TeamMessageRemovalEntity
{
    public Guid Id { get; set; }

    public TeamMessageId TeamMessageId { get; set; }

    public SiteId SiteId { get; set; }

    public OperatorId RemovedByOperatorId { get; set; }

    public DateTimeOffset RemovedAt { get; set; }
}
