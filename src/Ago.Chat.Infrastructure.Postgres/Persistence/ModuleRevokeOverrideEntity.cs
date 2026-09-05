using Ago.Chat.Domain;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `23-13`: exists solely so `dotnet ef migrations add` can generate `module_revoke_overrides`' own
/// `CREATE TABLE` from a declarative model - the identical "migration-scaffolding only, nothing ever
/// queries this DbSet" shape <see cref="ExportRequestEntity"/>/<see cref="AccessRecordEntity"/>'s own
/// remarks give in full (`db-migration` skill: "update the entity and its
/// `IEntityTypeConfiguration`... add the migration"). <see cref="ModuleRevokeOverrideRepository"/> is
/// raw Npgsql end to end, for the identical reason those two give: a one-row-per-event record with no
/// aggregate behind it has nothing an EF change-tracked load-mutate-save buys it. Written once, by the
/// handler that exercised the override, and never touched again.
///
/// <para><b>No FK on <see cref="SiteId"/>, deliberately - the fourth instance of
/// `adr/0111`/`adr/0112`/`adr/0113`'s own mechanism</b>, restated once more:
/// <see cref="ModuleRevokeOverrideEntityConfiguration"/>'s own remarks give the full reasoning.</para>
/// </summary>
internal sealed class ModuleRevokeOverrideEntity
{
    public Guid Id { get; set; }

    public SiteId SiteId { get; set; }

    public string ModuleKey { get; set; } = string.Empty;

    public string RevokedBy { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public DateTimeOffset RevokedAt { get; set; }
}
