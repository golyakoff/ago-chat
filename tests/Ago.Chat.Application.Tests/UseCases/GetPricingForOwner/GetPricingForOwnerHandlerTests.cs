using Ago.Chat.Application.UseCases.CreateCheckoutSession;
using Ago.Chat.Application.UseCases.GetPricingForOwner;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetPricingForOwner;

/// <summary>
/// `25-20`: unlike its sibling owner reads (`ListSitesForOwnerHandler`, `GetSiteForOwnerHandler`,
/// neither of which has an Application-level unit test - both need a real database to say anything),
/// <see cref="GetPricingForOwnerHandler"/> touches no I/O at all: every number it returns comes from
/// an in-process <see cref="BillingOptions"/> instance and a handful of `Ago.Chat.Domain` constants.
/// That is exactly `testing.md`'s own boundary for a plain unit test - no fake, no database, no HTTP
/// host - so this class exists where its two siblings' equivalent behaviour instead lives only in
/// `Ago.Chat.Integration.Tests.OwnerPricingEndpointTests`.
/// </summary>
public sealed class GetPricingForOwnerHandlerTests
{
    [Fact]
    public async Task HandleAsync_ReturnsTheRealSeatPricingFromBillingOptionsAndDomainConstants()
    {
        var billingOptions = new BillingOptions
        {
            PricePerSeatRub = 590m,
            CheckoutReturnUrl = "https://office.test.invalid/settings/billing",
        };
        var handler = new GetPricingForOwnerHandler(billingOptions);

        var response = await handler.HandleAsync(CancellationToken.None);

        Assert.Equal(590m, response.SeatPricing.PricePerSeatRub);
        Assert.Equal(BillingSubscription.PeriodLength.TotalDays, response.SeatPricing.BillingPeriodDays);
        Assert.Equal(SubscriptionTierBands.FreeSeatsIncluded, response.SeatPricing.FreeSeatsIncluded);
    }

    [Fact]
    public async Task HandleAsync_ListsBothTiers_InAscendingOrder_MatchingSubscriptionTierBands()
    {
        var handler = new GetPricingForOwnerHandler(new BillingOptions { PricePerSeatRub = 1m });

        var response = await handler.HandleAsync(CancellationToken.None);

        Assert.Collection(
            response.SeatPricing.Tiers,
            starter =>
            {
                Assert.Equal(SubscriptionTierBands.Starter, starter.Key);
                Assert.Equal(SubscriptionTierBands.MinSeats, starter.MinSeats);
                Assert.Equal(SubscriptionTierBands.GrowthMinSeats - 1, starter.MaxSeats);
            },
            growth =>
            {
                Assert.Equal(SubscriptionTierBands.Growth, growth.Key);
                Assert.Equal(SubscriptionTierBands.GrowthMinSeats, growth.MinSeats);
                Assert.Equal(SubscriptionTierBands.MaxSeats, growth.MaxSeats);
            });

        // Every seat count SubscriptionTierBands.TryResolveTier actually resolves must fall inside
        // exactly one of the two tier rows this handler reports - the two sources must never disagree
        // about where a boundary sits.
        for (var seats = SubscriptionTierBands.MinSeats; seats <= SubscriptionTierBands.MaxSeats; seats++)
        {
            SubscriptionTierBands.TryResolveTier(seats, out var expectedTier);
            var matching = Assert.Single(response.SeatPricing.Tiers, t => seats >= t.MinSeats && seats <= t.MaxSeats);
            Assert.Equal(expectedTier, matching.Key);
        }
    }

    // `25-20`'s own honest finding, pinned at the unit level too (Ago.Chat.Integration.Tests.
    // OwnerPricingEndpointTests proves it end to end over real HTTP): no billing option carries a
    // price anywhere in this codebase today, so this handler never fabricates a list - see the
    // handler's own remarks for why this is not read from a configuration section.
    [Fact]
    public async Task HandleAsync_ReturnsNoBillingOptions_NoMechanismExistsToPriceThemYet()
    {
        var handler = new GetPricingForOwnerHandler(new BillingOptions { PricePerSeatRub = 1m });

        var response = await handler.HandleAsync(CancellationToken.None);

        Assert.Empty(response.BillingOptions);
    }
}
