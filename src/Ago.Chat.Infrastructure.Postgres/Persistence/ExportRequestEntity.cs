using Ago.Chat.Domain;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `16-03`: exists solely so `dotnet ef migrations add` can generate `export_requests`' own
/// `CREATE TABLE` from a declarative model, the same reason <see cref="RoleRecord"/>/
/// <see cref="OperatorRoleRecord"/> exist as plain persistence records with no Domain counterpart
/// (`db-migration` skill: "update the entity and its `IEntityTypeConfiguration`... add the
/// migration"). <b>Unlike those two, nothing ever queries this type through EF</b> - <see
/// cref="ExportRequestRepository"/> is raw Npgsql end to end, for the same reason
/// <see cref="Ago.Chat.Application.Abstractions.IExportRequestRepository"/>'s own remarks give: a
/// request/status row with no aggregate has nothing an EF change-tracked load-mutate-save buys it, and
/// `Ago.Chat.Worker`'s `SiteExportJob` needs a forward-only reader over the same table regardless
/// (`SiteExportQuery`). This is one step further than `Site`'s own `ErasureRequestedAt` shadow
/// property (declared on an entity that otherwise *is* loaded through EF) - here the whole entity is
/// migration-only. Kept as a full EF entity rather than a hand-written migration because
/// `dotnet ef migrations add` then applies this project's own naming/conversion conventions
/// (<see cref="IdConverters.Site"/>, the FK, the partial index) exactly as it would for any other
/// table, instead of a second, hand-maintained source of truth for those conventions.
/// </summary>
internal sealed class ExportRequestEntity
{
    public Guid Id { get; set; }

    public SiteId SiteId { get; set; }

    public Guid RequestedBy { get; set; }

    public string Status { get; set; } = nameof(ExportStatus.Pending);

    public string? ObjectKey { get; set; }

    public string? FailureReason { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}
