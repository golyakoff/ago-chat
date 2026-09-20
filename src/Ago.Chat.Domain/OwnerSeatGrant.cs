namespace Ago.Chat.Domain;

/// <summary>`25-181`: which capacity an <see cref="OwnerSeatGrant"/> adds to - a Chat-owned enum, not
/// the seeded `roles` table's own free-form <c>Name</c> (`"Operator"`/`"Admin"`): this type has to
/// exist and compile before any particular site has seeded a role at all, and the seat-limit split it
/// names (<see cref="Site.SeatLimit"/> vs <see cref="Site.AdminLimit"/>) is a fixed, code-level fact,
/// not a per-site configurable name the way a custom role might one day be.</summary>
public enum OwnerSeatGrantRole
{
    Operator,
    Administrator,
}

/// <summary>
/// `25-181`: "the platform owner granted site X, Q extra seats of role R, by hand" - the identical
/// shape <see cref="ModuleQuantityGrant.SetUnconditionalGrant"/>'s own precedent already established
/// for "the owner grants something extra, with who/why recorded and an optional expiry"
/// (`23-86`/`25-115`), reused here rather than reinvented (the author's own explicit ask, "для
/// однообразия - давай добавим").
///
/// <para><b>A new type, not a role-scoped extension of <see cref="Site.SeatLimit"/>/<see cref="Site.AdminLimit"/>.</b>
/// This item's own backlog names the choice explicitly and leaves it open; the deciding fact is that
/// those two fields are <em>stored snapshots</em>, rewritten only by <see cref="Site.ActivateSubscription"/>
/// on a real billing event - there is no write path that runs "the moment expiry passes" the way
/// <see cref="EffectiveQuantity"/> is required to (CLAUDE.md rule 11: no background job "revokes"
/// anything). Folding this into <see cref="Site.AdminLimit"/> would mean either persisting a limit that
/// silently goes stale the instant the clock passes <see cref="ExpiresAt"/> (the exact "cached what a
/// write decision depends on" rule 8 forbids), or adding a background sweep to keep it fresh (the exact
/// thing the backlog rules out). A second, independent row - read fresh, on every check, exactly the
/// posture <see cref="ModuleQuantityGrant"/>'s own remarks already state for itself ("computed fresh,
/// never persisted as a stale fact") - has neither problem: the current limit for a role is always
/// <c>Site.SeatLimit-or-AdminLimit + OwnerSeatGrant.EffectiveQuantity(now)</c>, computed at the moment
/// something asks, never stored as a third, competing fact.</para>
///
/// <para><b>One row per (site, role), a snapshot, never a delta or a history</b> - the identical reason
/// <see cref="ModuleQuantityGrant"/>'s own remarks give for itself: message order is only guaranteed
/// per partition (CLAUDE.md rule 6), so a caller re-granting the same role always replaces the prior
/// grant outright (<see cref="Grant"/> when none exists yet, or a caller re-invoking with a fresh
/// quantity/reason/expiry when one already does) rather than accumulating.</para>
///
/// <para><see cref="GrantedBy"/> is the platform owner's own Keycloak <c>sub</c>, a raw string - the
/// identical <see cref="ModuleQuantityGrant.UnconditionalGrantSetBy"/> reasoning applies unchanged: the
/// platform owner has no row in this site's own operator roster (`adr/0032`).</para>
/// </summary>
public sealed class OwnerSeatGrant
{
    public const int MinQuantity = 1;

    public const int MaxQuantity = 5;

    public SiteId SiteId { get; }

    public OwnerSeatGrantRole Role { get; }

    public int Quantity { get; private set; }

    public string GrantedBy { get; private set; } = string.Empty;

    public string Reason { get; private set; } = string.Empty;

    public DateTimeOffset GrantedAt { get; private set; }

    /// <summary><see langword="null"/> means "бессрочно" (indefinite) - the identical "absent, not a
    /// sentinel" convention <see cref="ModuleQuantityGrant.UnconditionalGrantExpiresAt"/> already
    /// uses.</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    private OwnerSeatGrant(SiteId siteId, OwnerSeatGrantRole role)
    {
        SiteId = siteId;
        Role = role;
    }

    // EF Core materialization only - never called by domain code.
    private OwnerSeatGrant()
    {
    }

    public static OwnerSeatGrant Grant(
        SiteId siteId, OwnerSeatGrantRole role, int quantity, string grantedBy, string reason,
        DateTimeOffset now, DateTimeOffset? expiresAt = null)
    {
        var grant = new OwnerSeatGrant(siteId, role);
        grant.SetGrant(quantity, grantedBy, reason, now, expiresAt);
        return grant;
    }

    /// <summary>Replaces the granted number, who granted it, why, and its expiry, all at once - never a
    /// partial update, the identical "one call states the whole new fact" shape
    /// <see cref="ModuleQuantityGrant.SetUnconditionalGrant"/> already uses for its own who/why/expiry
    /// triple.</summary>
    public void SetGrant(
        int quantity, string grantedBy, string reason, DateTimeOffset now, DateTimeOffset? expiresAt = null)
    {
        if (quantity < MinQuantity || quantity > MaxQuantity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(quantity), quantity, $"An owner seat grant must be between {MinQuantity} and {MaxQuantity}.");
        }

        if (string.IsNullOrWhiteSpace(grantedBy))
        {
            throw new ArgumentException("An owner seat grant must name who granted it.", nameof(grantedBy));
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException(
                "A reason is required whenever the platform owner grants extra seats.", nameof(reason));
        }

        Quantity = quantity;
        GrantedBy = grantedBy;
        Reason = reason.Trim();
        GrantedAt = now;
        ExpiresAt = expiresAt;
    }

    /// <summary>`25-181`: the live read - <see cref="Quantity"/> when <see cref="ExpiresAt"/> is absent
    /// or still in the future, <c>0</c> the instant it has passed. A method, not a property, for the
    /// identical reason <see cref="ModuleQuantityGrant.EffectiveQuantity"/> already is one: an expiry
    /// check needs a clock, and <paramref name="now"/> is always the caller's own <c>IClock.UtcNow</c>,
    /// never <see cref="DateTimeOffset.UtcNow"/> read from inside Domain (CLAUDE.md rules 2/11).
    /// Non-strict boundary (<c>expiresAt &lt;= now</c>) - the identical convention
    /// <see cref="ModuleQuantityGrant.EffectiveQuantity"/> and <c>EnabledModuleReadStore</c>'s own
    /// `expires_at &lt;= @Now` both already use.</summary>
    public int EffectiveQuantity(DateTimeOffset now) =>
        ExpiresAt is { } expiresAt && expiresAt <= now ? 0 : Quantity;
}
