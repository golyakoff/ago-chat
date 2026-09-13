namespace Ago.Chat.Domain.Tests;

/// <summary>`22-07`: "site X's module K has quantity Q" - the calendar add-on's own "N masters" is
/// the first real instance. See <see cref="ModuleQuantityGrant"/>'s own remarks for why it is a
/// snapshot rather than a delta.</summary>
public class ModuleQuantityGrantTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Grant_SetsTheQuantity()
    {
        var grant = ModuleQuantityGrant.Grant(SiteId, new ModuleKey("calendar"), 5, Now);

        Assert.Equal(5, grant.Quantity);
        Assert.Equal(Now, grant.GrantedAt);
        Assert.Equal(SiteId, grant.SiteId);
        Assert.Equal(new ModuleKey("calendar"), grant.ModuleKey);
    }

    [Fact]
    public void Grant_RejectsANegativeQuantity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ModuleQuantityGrant.Grant(SiteId, new ModuleKey("calendar"), -1, Now));
    }

    [Fact]
    public void SetQuantity_ReplacesTheNumberAndTheTimestamp()
    {
        var grant = ModuleQuantityGrant.Grant(SiteId, new ModuleKey("calendar"), 5, Now);

        grant.SetQuantity(2, Now.AddDays(1));

        Assert.Equal(2, grant.Quantity);
        Assert.Equal(Now.AddDays(1), grant.GrantedAt);
    }

    [Fact]
    public void SetQuantity_IsASnapshotNotADelta_ReapplyingTheSameValueIsANoOp()
    {
        var grant = ModuleQuantityGrant.Grant(SiteId, new ModuleKey("calendar"), 5, Now);

        grant.SetQuantity(5, Now.AddSeconds(1));

        Assert.Equal(5, grant.Quantity);
    }

    [Fact]
    public void SetQuantity_RejectsANegativeQuantity()
    {
        var grant = ModuleQuantityGrant.Grant(SiteId, new ModuleKey("calendar"), 5, Now);

        Assert.Throws<ArgumentOutOfRangeException>(() => grant.SetQuantity(-1, Now));
    }

    // `23-86`: EffectiveQuantity/SetUnconditionalGrant - the platform owner's own unconditional-grant
    // flag, OR'd with whatever Quantity billing (or an owner's own quantity grant) last wrote. See
    // ModuleQuantityGrant's own remarks for why the two never collapse into one write.

    [Fact]
    public void EffectiveQuantity_WithNoUnconditionalGrant_IsPlainQuantity()
    {
        var grant = ModuleQuantityGrant.Grant(SiteId, new ModuleKey("channel"), 0, Now);

        Assert.Equal(0, grant.EffectiveQuantity);
    }

    [Fact]
    public void EffectiveQuantity_FlagSet_QuantityZero_FloorsAtOne()
    {
        var grant = ModuleQuantityGrant.Grant(SiteId, new ModuleKey("channel"), 0, Now);

        grant.SetUnconditionalGrant(true, "owner-sub", "trial while support investigates", Now);

        Assert.Equal(1, grant.EffectiveQuantity);
        // Quantity itself is untouched - the flag is a second, independent input, never a second write
        // to the same field.
        Assert.Equal(0, grant.Quantity);
    }

    [Fact]
    public void EffectiveQuantity_FlagSet_QuantityAlreadyPositive_NeverShrinksARealQuantity()
    {
        var grant = ModuleQuantityGrant.Grant(SiteId, new ModuleKey("calendar"), 5, Now);

        grant.SetUnconditionalGrant(true, "owner-sub", "owner override", Now);

        Assert.Equal(5, grant.EffectiveQuantity);
    }

    [Fact]
    public void EffectiveQuantity_FlagSetThenLifted_QuantityStillZero_GoesOff()
    {
        var grant = ModuleQuantityGrant.Grant(SiteId, new ModuleKey("channel"), 0, Now);
        grant.SetUnconditionalGrant(true, "owner-sub", "trial", Now);
        Assert.Equal(1, grant.EffectiveQuantity);

        grant.SetUnconditionalGrant(false, "owner-sub", "trial period ended, tenant never paid", Now.AddDays(1));

        Assert.Equal(0, grant.EffectiveQuantity);
    }

    [Fact]
    public void EffectiveQuantity_FlagSetThenLifted_QuantityStillPositive_SurvivesOnBillingAlone()
    {
        var grant = ModuleQuantityGrant.Grant(SiteId, new ModuleKey("channel"), 0, Now);
        grant.SetUnconditionalGrant(true, "owner-sub", "trial", Now);
        // The tenant paid while the flag was set - a billing-driven write, exactly like
        // SubscriptionRenewalApplier's own GrantAsync call, indifferent to the flag.
        grant.SetQuantity(1, Now.AddDays(1));

        grant.SetUnconditionalGrant(false, "owner-sub", "billing now covers it", Now.AddDays(2));

        Assert.Equal(1, grant.EffectiveQuantity);
    }

    [Fact]
    public void SetUnconditionalGrant_RecordsProvenance()
    {
        var grant = ModuleQuantityGrant.Grant(SiteId, new ModuleKey("channel"), 0, Now);

        grant.SetUnconditionalGrant(true, "owner-sub-123", "sales trial", Now);

        Assert.True(grant.UnconditionallyGrantedByOwner);
        Assert.Equal("owner-sub-123", grant.UnconditionalGrantSetBy);
        Assert.Equal("sales trial", grant.UnconditionalGrantReason);
        Assert.Equal(Now, grant.UnconditionalGrantSetAt);
    }

    [Fact]
    public void SetUnconditionalGrant_TrimsTheReason()
    {
        var grant = ModuleQuantityGrant.Grant(SiteId, new ModuleKey("channel"), 0, Now);

        grant.SetUnconditionalGrant(true, "owner-sub", "  sales trial  ", Now);

        Assert.Equal("sales trial", grant.UnconditionalGrantReason);
    }

    [Fact]
    public void SetUnconditionalGrant_RejectsABlankReason()
    {
        var grant = ModuleQuantityGrant.Grant(SiteId, new ModuleKey("channel"), 0, Now);

        Assert.Throws<ArgumentException>(() => grant.SetUnconditionalGrant(true, "owner-sub", "   ", Now));
    }

    [Fact]
    public void SetUnconditionalGrant_RejectsABlankSetBy()
    {
        var grant = ModuleQuantityGrant.Grant(SiteId, new ModuleKey("channel"), 0, Now);

        Assert.Throws<ArgumentException>(() => grant.SetUnconditionalGrant(true, "  ", "a reason", Now));
    }
}
