namespace Ago.Chat.Domain.Tests;

/// <summary>`25-181`: "the platform owner granted this site Q extra seats of role R, by hand" - the
/// identical who/reason/expiry/live-clock-check shape <see cref="ModuleQuantityGrantTests"/> already
/// proves for <see cref="ModuleQuantityGrant"/>, restated here for the new type.</summary>
public class OwnerSeatGrantTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Grant_SetsTheGrant()
    {
        var grant = OwnerSeatGrant.Grant(SiteId, OwnerSeatGrantRole.Administrator, 2, "owner-sub", "incident cover", Now);

        Assert.Equal(SiteId, grant.SiteId);
        Assert.Equal(OwnerSeatGrantRole.Administrator, grant.Role);
        Assert.Equal(2, grant.Quantity);
        Assert.Equal("owner-sub", grant.GrantedBy);
        Assert.Equal("incident cover", grant.Reason);
        Assert.Equal(Now, grant.GrantedAt);
        Assert.Null(grant.ExpiresAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public void Grant_RejectsAQuantityOutsideOneToFive(int quantity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => OwnerSeatGrant.Grant(SiteId, OwnerSeatGrantRole.Operator, quantity, "owner-sub", "a reason", Now));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void Grant_AcceptsTheBoundaryQuantities(int quantity)
    {
        var grant = OwnerSeatGrant.Grant(SiteId, OwnerSeatGrantRole.Operator, quantity, "owner-sub", "a reason", Now);

        Assert.Equal(quantity, grant.Quantity);
    }

    [Fact]
    public void Grant_RejectsABlankGrantedBy()
    {
        Assert.Throws<ArgumentException>(
            () => OwnerSeatGrant.Grant(SiteId, OwnerSeatGrantRole.Operator, 1, "  ", "a reason", Now));
    }

    [Fact]
    public void Grant_RejectsABlankReason()
    {
        Assert.Throws<ArgumentException>(
            () => OwnerSeatGrant.Grant(SiteId, OwnerSeatGrantRole.Operator, 1, "owner-sub", "   ", Now));
    }

    [Fact]
    public void Grant_TrimsTheReason()
    {
        var grant = OwnerSeatGrant.Grant(SiteId, OwnerSeatGrantRole.Operator, 1, "owner-sub", "  padded  ", Now);

        Assert.Equal("padded", grant.Reason);
    }

    [Fact]
    public void SetGrant_ReplacesTheWholeFact_NotADelta()
    {
        var grant = OwnerSeatGrant.Grant(SiteId, OwnerSeatGrantRole.Operator, 5, "owner-sub", "first reason", Now);

        grant.SetGrant(2, "owner-sub-2", "second reason", Now.AddDays(1), Now.AddDays(30));

        Assert.Equal(2, grant.Quantity);
        Assert.Equal("owner-sub-2", grant.GrantedBy);
        Assert.Equal("second reason", grant.Reason);
        Assert.Equal(Now.AddDays(1), grant.GrantedAt);
        Assert.Equal(Now.AddDays(30), grant.ExpiresAt);
    }

    // EffectiveQuantity - the live, caller-clocked read. Mirrors ModuleQuantityGrantTests' own three
    // cases: no expiry, a future expiry, a past one, plus the exact boundary.

    [Fact]
    public void EffectiveQuantity_WithNoExpiry_StaysGranted_IndefinitelyIntoTheFuture()
    {
        var grant = OwnerSeatGrant.Grant(SiteId, OwnerSeatGrantRole.Administrator, 3, "owner-sub", "бессрочно", Now);

        Assert.Equal(3, grant.EffectiveQuantity(Now.AddYears(10)));
    }

    [Fact]
    public void EffectiveQuantity_WithAFutureExpiry_StaysGranted_BeforeTheExpiry()
    {
        var expiresAt = Now.AddDays(30);
        var grant = OwnerSeatGrant.Grant(SiteId, OwnerSeatGrantRole.Administrator, 1, "owner-sub", "30-day cover", Now, expiresAt);

        Assert.Equal(1, grant.EffectiveQuantity(Now.AddDays(29)));
    }

    /// <summary>The Done-when's own headline proof: grant, confirm the raised limit, advance a fake
    /// clock past the expiry, confirm it drops back - here at the domain level, where the limit is
    /// exactly <see cref="EffectiveQuantity"/>'s own return value.</summary>
    [Fact]
    public void EffectiveQuantity_OnceTheExpiryPasses_DropsBackToZero()
    {
        var expiresAt = Now.AddDays(30);
        var grant = OwnerSeatGrant.Grant(SiteId, OwnerSeatGrantRole.Administrator, 1, "owner-sub", "30-day cover", Now, expiresAt);
        Assert.Equal(1, grant.EffectiveQuantity(Now));

        var afterExpiry = expiresAt.AddSeconds(1);

        Assert.Equal(0, grant.EffectiveQuantity(afterExpiry));
    }

    [Fact]
    public void EffectiveQuantity_AtExactlyTheExpiryMoment_IsAlreadyExpired()
    {
        var expiresAt = Now.AddDays(30);
        var grant = OwnerSeatGrant.Grant(SiteId, OwnerSeatGrantRole.Operator, 2, "owner-sub", "trial", Now, expiresAt);

        Assert.Equal(0, grant.EffectiveQuantity(expiresAt));
    }
}
