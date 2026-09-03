namespace Ago.Chat.Domain.Tests;

/// <summary>`13-02`/`13-08`: the non-overlapping band boundary this item's own backlog reading
/// establishes - Starter = 3-9, Growth = 10-100, stated explicitly here so a boundary value going the
/// wrong way fails loudly rather than silently. `13-08` moved Starter's own floor from 2 to 3 when the
/// free tier's own ceiling grew from one seat to two - see <see cref="SubscriptionTierBands"/>'s own
/// remarks for why the two numbers move together.</summary>
public class SubscriptionTierBandsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(101)]
    [InlineData(-1)]
    public void TryResolveTier_WhenSeatsOutsideTheBandTable_Fails(int seats)
    {
        var resolved = SubscriptionTierBands.TryResolveTier(seats, out var tier);

        Assert.False(resolved);
        Assert.Equal(string.Empty, tier);
    }

    // `13-08`: 2 seats is where the free tier's own ceiling sits, never a purchasable count - a
    // dedicated fact alongside the theory above so this specific boundary (the one this item actually
    // moved) fails loudly and by name, not just as one more number in a shared InlineData list.
    [Fact]
    public void TryResolveTier_AtTheFreeTierCeiling_TwoSeatsFails()
    {
        var resolved = SubscriptionTierBands.TryResolveTier(2, out var tier);

        Assert.False(resolved);
        Assert.Equal(string.Empty, tier);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(9)]
    public void TryResolveTier_WhenSeatsAreThreeToNine_ResolvesStarter(int seats)
    {
        var resolved = SubscriptionTierBands.TryResolveTier(seats, out var tier);

        Assert.True(resolved);
        Assert.Equal(SubscriptionTierBands.Starter, tier);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(100)]
    public void TryResolveTier_WhenSeatsAreTenToOneHundred_ResolvesGrowth(int seats)
    {
        var resolved = SubscriptionTierBands.TryResolveTier(seats, out var tier);

        Assert.True(resolved);
        Assert.Equal(SubscriptionTierBands.Growth, tier);
    }

    // The literal boundary the backlog's own overlapping band text made ambiguous - 9 is the last
    // Starter seat count and 10 is the first Growth one, proven as two separate facts so a future
    // off-by-one regresses loudly.
    [Fact]
    public void TryResolveTier_AtTheBoundary_NineIsStarterAndTenIsGrowth()
    {
        SubscriptionTierBands.TryResolveTier(9, out var nine);
        SubscriptionTierBands.TryResolveTier(10, out var ten);

        Assert.Equal(SubscriptionTierBands.Starter, nine);
        Assert.Equal(SubscriptionTierBands.Growth, ten);
    }
}
