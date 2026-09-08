using Ago.Chat.Domain;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `23-72`: exists solely so `dotnet ef migrations add` can generate `role_change_records`' own `CREATE
/// TABLE` - the same "migration-scaffolding only, nothing ever queries this DbSet" shape
/// <see cref="AccessRecordEntity"/>/<see cref="ExportRequestEntity"/> already establish.
/// <see cref="RoleChangeRecordRepository"/> writes through <see cref="AgoChatDbContext"/> directly via
/// <c>ExecuteSqlInterpolatedAsync</c>, not through this entity's own change tracking - see that class's
/// own remarks for why (it must join the caller's ambient transaction, the opposite reason
/// <see cref="AccessRecordRepository"/> gives for its own fresh connection).
///
/// <para><b>Real foreign keys, unlike <see cref="AccessRecordEntity"/>.</b> This table is ordinary
/// tenant-owned data - a record of one tenant's own internal administration, not evidence of an outside
/// party's access that must outlive the tenant's own erasure the way `access_records` must. It cascades
/// with its site and its two named operators the same way `operator_roles` already does
/// (<see cref="OperatorRoleRecordConfiguration"/>'s own remarks), rather than deliberately surviving
/// them.</para>
/// </summary>
internal sealed class RoleChangeRecordEntity
{
    public Guid Id { get; set; }

    public SiteId SiteId { get; set; }

    public OperatorId ChangedByOperatorId { get; set; }

    public OperatorId ChangedOperatorId { get; set; }

    public List<string> PreviousRoleNames { get; set; } = [];

    public string NewRoleName { get; set; } = string.Empty;

    public DateTimeOffset ChangedAt { get; set; }
}
