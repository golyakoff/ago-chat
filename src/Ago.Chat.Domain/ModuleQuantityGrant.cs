namespace Ago.Chat.Domain;

/// <summary>
/// `22-07`/`adr/0093`: "site X has bought Q of whatever module K's own countable dimension is" - the
/// generic shape the calendar add-on's own "N masters" is the first real instance of. Deliberately
/// opaque, the identical discipline <see cref="ModuleKey"/>'s own remarks state for the key itself:
/// this type stores a number and knows nothing about what it counts. A module interprets its own
/// quantity; chat only carries it and tells the module it changed.
///
/// <para><b>A snapshot, one row per (site, module) - never a delta, never a history.</b> The same
/// shape <c>RoleAssignmentsChanged</c> chose for the identical reason (that event's own remarks):
/// ordering is only guaranteed per partition key (rule 6), so a consumer applying "+2"/"-1" facts out
/// of order could land on the wrong number forever with no way to notice. <see cref="SetQuantity"/>
/// always sets the *current* number, which is what makes a redelivery of the identical grant a
/// genuine no-op on the receiving end.</para>
///
/// <para>Keyed by <see cref="SiteId"/> and <see cref="ModuleKey"/> together, not by a synthetic id -
/// unlike <see cref="EnabledModule"/> (which mints a fresh <c>EnabledModuleId</c> per registration and
/// therefore has no enforced one-row-per-module uniqueness, a named, accepted limitation of that
/// type), this type's whole point is that there is exactly one grant per (site, module), so the
/// natural key <em>is</em> the identity - there is nothing a synthetic id would add.</para>
/// </summary>
public sealed class ModuleQuantityGrant
{
    public SiteId SiteId { get; }

    public ModuleKey ModuleKey { get; }

    public int Quantity { get; private set; }

    public DateTimeOffset GrantedAt { get; private set; }

    private ModuleQuantityGrant(SiteId siteId, ModuleKey moduleKey, int quantity, DateTimeOffset grantedAt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(quantity);
        SiteId = siteId;
        ModuleKey = moduleKey;
        Quantity = quantity;
        GrantedAt = grantedAt;
    }

    // EF Core materialization only - never called by domain code.
    private ModuleQuantityGrant()
    {
    }

    public static ModuleQuantityGrant Grant(SiteId siteId, ModuleKey moduleKey, int quantity, DateTimeOffset now) =>
        new(siteId, moduleKey, quantity, now);

    /// <summary>Replaces the granted number outright - never incremented/decremented, for the
    /// snapshot reason this type's own remarks give.</summary>
    public void SetQuantity(int quantity, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(quantity);
        Quantity = quantity;
        GrantedAt = now;
    }
}
