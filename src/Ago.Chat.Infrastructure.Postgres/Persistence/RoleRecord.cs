using Ago.Chat.Domain;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// The RBAC schema adr/0016 needs, with no Domain or Application equivalent - nothing above
/// <see cref="PermissionChecker"/> manages roles yet, so there is nothing for a richer model to buy
/// (backlog `1-04`). A plain persistence record, not an entity with invariants. <see cref="SiteId"/>
/// uses the Domain id type (not a raw <see cref="Guid"/>) only because EF requires the FK and
/// principal key's CLR types to match for the relationship in <see cref="RoleRecordConfiguration"/>.
/// </summary>
internal sealed class RoleRecord
{
    public Guid Id { get; set; }
    public SiteId SiteId { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<string> Permissions { get; set; } = [];
}
