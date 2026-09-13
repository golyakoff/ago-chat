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
///
/// <para><b>`23-86`: <see cref="UnconditionallyGrantedByOwner"/> is a second, independent input to
/// what this row means - never a second, competing write to <see cref="Quantity"/> itself.</b> The
/// three awkward cases this item's own "Answered" section settles: a trial the owner granted by hand,
/// followed by a real payment; the same trial, followed by that payment lapsing; and a flag the owner
/// later lifts once billing alone should decide again. <see cref="Quantity"/> keeps meaning exactly
/// what it always has - the last number <see cref="SetQuantity"/> was called with, which for a
/// billing-driven option is <see cref="Infrastructure.Postgres.SubscriptionRenewalApplier"/>'s own
/// binary Granted/Revoked snapshot (that type's own remarks) - while <see cref="EffectiveQuantity"/> is
/// the two combined by OR, computed fresh from whatever this row currently holds, never itself stored.
/// A billing lapse while the flag is set still writes <see cref="Quantity"/> back to zero exactly as it
/// always did (<see cref="SubscriptionRenewalApplier.RevokeEntitlementAsync"/> is unchanged by this
/// item and does not need to know this field exists), and <see cref="EffectiveQuantity"/> is what stays
/// at one throughout - the flag protects the *reading*, not the billing-driven write underneath
/// it.</para>
/// </summary>
public sealed class ModuleQuantityGrant
{
    public SiteId SiteId { get; }

    public ModuleKey ModuleKey { get; }

    public int Quantity { get; private set; }

    public DateTimeOffset GrantedAt { get; private set; }

    /// <summary>`23-86`: <see langword="true"/> when the platform owner has unconditionally granted
    /// this (site, module) entitlement, independent of whatever <see cref="Quantity"/> billing last
    /// wrote. Settable only through <see cref="SetUnconditionalGrant"/>, which only the platform
    /// owner's own write surface calls - the identical "the distinction has to be *recorded*, not
    /// *felt*" reasoning <see cref="EnabledModule.GrantedByOwner"/>'s own remarks state for the
    /// analogous module-registry flag.</summary>
    public bool UnconditionallyGrantedByOwner { get; private set; }

    /// <summary>The Keycloak <c>sub</c> of the platform owner who last set or lifted
    /// <see cref="UnconditionallyGrantedByOwner"/>, or <see langword="null"/> if it has never been
    /// touched - a raw string, never <see cref="OperatorId"/>: the platform owner is a cross-tenant
    /// identity with no row in this site's own operator roster, the identical
    /// "<c>revokedBy</c> is a plain string, not a domain id" shape
    /// <c>Application.Abstractions.IModuleRevokeOverrideRepository.RecordAsync</c> already uses for the
    /// same reason (adr/0118).</summary>
    public string? UnconditionalGrantSetBy { get; private set; }

    /// <summary>The stated reason for the most recent <see cref="SetUnconditionalGrant"/> call -
    /// required, non-blank, whenever the flag is set *or* lifted (`adr/0118`'s own "a blank reason is
    /// the same failure as a defaulted expiry", mirrored here rather than reinvented). Free text, the
    /// identical bound <c>RevokeModuleForSiteAsOwnerHandler.MaxReasonLength</c> already uses.</summary>
    public string? UnconditionalGrantReason { get; private set; }

    /// <summary><see langword="null"/> until the first <see cref="SetUnconditionalGrant"/> call - the
    /// flag's own audit timestamp, deliberately separate from <see cref="GrantedAt"/> (which tracks the
    /// unrelated <see cref="Quantity"/> writes billing and the owner's quantity handlers make).</summary>
    public DateTimeOffset? UnconditionalGrantSetAt { get; private set; }

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

    /// <summary>`23-86`: the platform owner's own write - sets or lifts
    /// <see cref="UnconditionallyGrantedByOwner"/>, always with who and why. Never touches
    /// <see cref="Quantity"/>/<see cref="GrantedAt"/>: this type's own remarks state why the two
    /// inputs stay independent rather than one overwriting the other.</summary>
    public void SetUnconditionalGrant(bool unconditionallyGranted, string setBy, string reason, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(setBy))
        {
            throw new ArgumentException("An unconditional grant must name who set it.", nameof(setBy));
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException(
                "A reason is required whenever the platform owner changes an unconditional grant.", nameof(reason));
        }

        UnconditionallyGrantedByOwner = unconditionallyGranted;
        UnconditionalGrantSetBy = setBy;
        UnconditionalGrantReason = reason.Trim();
        UnconditionalGrantSetAt = now;
    }

    /// <summary>`23-86`: what this (site, module) grant actually is right now - <see cref="Quantity"/>
    /// OR <see cref="UnconditionallyGrantedByOwner"/>, this item's own "Answered, 2026-09-13" section
    /// made mechanical. Never persisted (see <c>Infrastructure.Postgres.Persistence.ModuleQuantityGrantConfiguration</c>'s
    /// own <c>Ignore</c> call) - a derived read, computed fresh from whichever of the two inputs was
    /// written most recently, exactly what makes lifting the flag "re-evaluate billing at the moment
    /// the flag is lifted" (this item's own text) rather than replaying a stale snapshot.
    /// <see cref="Math.Max(int,int)"/> against <c>1</c>, not a hardcoded <c>1</c>: a genuinely
    /// quantity-valued grant (the calendar add-on's own "N masters") already carries a real number the
    /// flag must never shrink, so the flag only ever raises a floor, never lowers a real quantity
    /// already above it.</summary>
    public int EffectiveQuantity => UnconditionallyGrantedByOwner ? Math.Max(Quantity, 1) : Quantity;
}
