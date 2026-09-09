namespace Ago.Chat.Domain.Tests;

/// <summary>`25-29`: `ago-business` decision `0012`'s own Business band - 2-5 seats, one tier, no
/// "Growth" band any more. Replaces this file's own previous assertions, which pinned `0008`'s
/// superseded 3-9/10-100 split - see `SubscriptionTierBands`' own remarks for the full history.</summary>
public class SubscriptionTierBandsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)] // `25-29`: the first seat count past `0012`'s own Business band - "Premium" (6-10)
                    // is designed but deliberately unreleased (`0012`'s own §1c).
    [InlineData(10)]
    [InlineData(-1)]
    public void TryResolveTier_WhenSeatsOutsideTheBandTable_Fails(int seats)
    {
        var resolved = SubscriptionTierBands.TryResolveTier(seats, out var tier);

        Assert.False(resolved);
        Assert.Equal(string.Empty, tier);
    }

    // `25-29`: 2 seats used to be the free tier's own ceiling and unpurchasable (`13-08`) - `0012`
    // deliberately reopens that overlap, pricing Business at exactly 2 seats for reasons that have
    // nothing to do with seat count (`SubscriptionTierBands`' own remarks: permanent history, a
    // second Administrator, no auto-deletion). A dedicated fact alongside the theory below so this
    // specific, deliberately-reversed boundary fails loudly and by name.
    [Fact]
    public void TryResolveTier_AtTheFreeTierCeiling_TwoSeatsNowResolvesStarter()
    {
        var resolved = SubscriptionTierBands.TryResolveTier(2, out var tier);

        Assert.True(resolved);
        Assert.Equal(SubscriptionTierBands.Starter, tier);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void TryResolveTier_WhenSeatsAreTwoToFive_ResolvesStarter(int seats)
    {
        var resolved = SubscriptionTierBands.TryResolveTier(seats, out var tier);

        Assert.True(resolved);
        Assert.Equal(SubscriptionTierBands.Starter, tier);
    }

    // `25-29`: the literal boundary `0012` actually draws - 5 is the last purchasable seat count, 6
    // fails, proven as two separate facts so a future off-by-one regresses loudly.
    [Fact]
    public void TryResolveTier_AtTheBoundary_FiveResolvesAndSixFails()
    {
        var fiveResolved = SubscriptionTierBands.TryResolveTier(5, out var five);
        var sixResolved = SubscriptionTierBands.TryResolveTier(6, out _);

        Assert.True(fiveResolved);
        Assert.Equal(SubscriptionTierBands.Starter, five);
        Assert.False(sixResolved);
    }

    // `25-29`: `Growth`/`GrowthMinSeats` stay defined for `RetentionClass`'s sake (that type's own
    // remarks), but no seat count reaches them any more through this method - `MaxSeats` (5) now sits
    // below `GrowthMinSeats` (10), so every seat count that once resolved Growth is out of range
    // entirely.
    [Theory]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(100)]
    public void TryResolveTier_NoSeatCountEverResolvesGrowthAnyMore(int seats)
    {
        var resolved = SubscriptionTierBands.TryResolveTier(seats, out var tier);

        Assert.False(resolved);
        Assert.Equal(string.Empty, tier);
    }

    // `25-29`: `ago-business` decision `0012`'s own Business price table, checked row by row - 2 and
    // 3 seats share the base price, 4 and 5 each add one marginal seat's worth. Uses the real decided
    // Roubles (490/200) rather than arbitrary test doubles, since this is the one test whose entire
    // point is proving the formula against the actual pricing document, not just its shape.
    [Theory]
    [InlineData(2, 490)]
    [InlineData(3, 490)]
    [InlineData(4, 690)]
    [InlineData(5, 890)]
    public void ComputeSeatPriceRub_MatchesAgoBusinessDecision0012_ForEveryPurchasableSeatCount(int seats, decimal expected)
    {
        var price = SubscriptionTierBands.ComputeSeatPriceRub(seats, baseSeatPriceRub: 490m, pricePerExtraSeatRub: 200m);

        Assert.Equal(expected, price);
    }

    [Fact]
    public void ComputeSeatPriceRub_AtOrBelowBaseSeats_ChargesOnlyTheBase()
    {
        var atBase = SubscriptionTierBands.ComputeSeatPriceRub(SubscriptionTierBands.BaseSeats, baseSeatPriceRub: 100m, pricePerExtraSeatRub: 10m);
        var belowBase = SubscriptionTierBands.ComputeSeatPriceRub(SubscriptionTierBands.BaseSeats - 1, baseSeatPriceRub: 100m, pricePerExtraSeatRub: 10m);

        Assert.Equal(100m, atBase);
        Assert.Equal(100m, belowBase);
    }

    [Fact]
    public void ComputeSeatPriceRub_PastBaseSeats_AddsTheMarginalRateOncePerExtraSeat()
    {
        var twoPastBase = SubscriptionTierBands.ComputeSeatPriceRub(SubscriptionTierBands.BaseSeats + 2, baseSeatPriceRub: 100m, pricePerExtraSeatRub: 10m);

        Assert.Equal(120m, twoPastBase);
    }

    // `25-20`/`25-29`: the free tier's own seat ceiling, no longer derived from `MinSeats` - the two
    // now happen to share a value because `0012` says so, not because one computes the other
    // (`SubscriptionTierBands`' own remarks). Pinned by value so a future edit to either literal is
    // caught here rather than silently reaching the owner's own price-list screen as a wrong number.
    [Fact]
    public void FreeSeatsIncluded_IsTwo_TheSameNumberAsMinSeats_ButNotDerivedFromIt()
    {
        Assert.Equal(2, SubscriptionTierBands.FreeSeatsIncluded);
        Assert.Equal(2, SubscriptionTierBands.MinSeats);
    }

    [Fact]
    public void MaxSeats_IsFive()
    {
        Assert.Equal(5, SubscriptionTierBands.MaxSeats);
    }

    [Fact]
    public void BaseSeats_IsThree()
    {
        Assert.Equal(3, SubscriptionTierBands.BaseSeats);
    }

    // `25-25`: `ago-business` decision `0012`'s own grid - Solo (free) includes one administrator,
    // Business includes two regardless of which seat band it splits into. Pinned by value, the same
    // "a future edit to either literal is caught here" reasoning the tests above state.
    [Fact]
    public void ResolveAdminLimit_ForFree_IsOne()
    {
        Assert.Equal(1, SubscriptionTierBands.ResolveAdminLimit("free"));
        Assert.Equal(SubscriptionTierBands.FreeAdminsIncluded, SubscriptionTierBands.ResolveAdminLimit("free"));
    }

    [Theory]
    [InlineData(SubscriptionTierBands.Starter)]
    [InlineData(SubscriptionTierBands.Growth)]
    public void ResolveAdminLimit_ForEveryPaidTier_IsTwo(string tier)
    {
        Assert.Equal(2, SubscriptionTierBands.ResolveAdminLimit(tier));
        Assert.Equal(SubscriptionTierBands.BusinessAdminsIncluded, SubscriptionTierBands.ResolveAdminLimit(tier));
    }
}
