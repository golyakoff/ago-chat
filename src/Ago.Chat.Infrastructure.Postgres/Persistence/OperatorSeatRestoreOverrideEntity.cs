using Ago.Chat.Domain;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `23-68`: exists solely so `dotnet ef migrations add` can generate `operator_seat_restore_overrides`'
/// own `CREATE TABLE` from a declarative model - the identical "migration-scaffolding only, nothing
/// ever queries this DbSet" shape <see cref="ModuleRevokeOverrideEntity"/>'s own remarks give in full.
/// <see cref="OperatorSeatRestoreOverrideRepository"/> is raw Npgsql end to end, for the identical
/// reason: a one-row-per-event record with no aggregate behind it has nothing an EF change-tracked
/// load-mutate-save buys it. Written once, by the handler that exercised the override, and never
/// touched again.
///
/// <para><b>No FK on <see cref="SiteId"/> or <see cref="OperatorId"/>, deliberately - the same
/// `adr/0111`/`adr/0112`/`adr/0113` mechanism <see cref="ModuleRevokeOverrideEntity"/>'s own remarks
/// restate once more.</b> <see cref="OperatorSeatRestoreOverrideEntityConfiguration"/>'s own remarks
/// give the full reasoning.</para>
/// </summary>
internal sealed class OperatorSeatRestoreOverrideEntity
{
    public Guid Id { get; set; }

    public SiteId SiteId { get; set; }

    public OperatorId OperatorId { get; set; }

    public string RestoredBy { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public DateTimeOffset RestoredAt { get; set; }
}
