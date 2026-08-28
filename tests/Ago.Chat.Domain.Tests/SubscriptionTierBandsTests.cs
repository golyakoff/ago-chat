namespace Ago.Chat.Domain.Tests;

/// <summary>`13-02`: the non-overlapping band boundary this item's own backlog reading establishes -
/// Starter = 2-9, Growth = 10-100, stated explicitly here so a boundary value going the wrong way fails
/// loudly rather than silently.</summary>
public class SubscriptionTierBandsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(101)]
    [InlineData(-1)]
    public void TryResolveTier_WhenSeatsOutsideTheBandTable_Fails(int seats)
    {
        var resolved = SubscriptionTierBands.TryResolveTier(seats, out var tier);

        Assert.False(resolved);
        Assert.Equal(string.Empty, tier);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(9)]
    public void TryResolveTier_WhenSeatsAreTwoToNine_ResolvesStarter(int seats)
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
