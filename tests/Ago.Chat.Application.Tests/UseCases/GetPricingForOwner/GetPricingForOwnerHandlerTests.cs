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
    // `25-29`: `ago-business` decision `0012`'s own base-plus-marginal formula, not `0008`'s
    // superseded flat rate - this test now checks the three fields that state that formula
    // completely, plus the legacy `PricePerSeatRub` field kept only for wire compatibility (see that
    // field's own remarks on `OwnerSeatPricingDto` for why it reports the marginal rate).
    [Fact]
    public async Task HandleAsync_ReturnsTheRealSeatPricingFromBillingOptionsAndDomainConstants()
    {
        var billingOptions = new BillingOptions
        {
            BaseSeatPriceRub = 490m,
            PricePerExtraSeatRub = 200m,
            CheckoutReturnUrl = "https://office.test.invalid/settings/billing",
        };
        var handler = new GetPricingForOwnerHandler(billingOptions);

        var response = await handler.HandleAsync(CancellationToken.None);

        Assert.Equal(200m, response.SeatPricing.PricePerSeatRub);
        Assert.Equal(SubscriptionTierBands.BaseSeats, response.SeatPricing.BaseSeats);
        Assert.Equal(490m, response.SeatPricing.BaseSeatPriceRub);
        Assert.Equal(200m, response.SeatPricing.PricePerExtraSeatRub);
        Assert.Equal(BillingSubscription.PeriodLength.TotalDays, response.SeatPricing.BillingPeriodDays);
        Assert.Equal(SubscriptionTierBands.FreeSeatsIncluded, response.SeatPricing.FreeSeatsIncluded);
    }

    // `25-29`: one tier, not two - `ago-business` decision `0012` prices exactly one Business band
    // (2-5 seats), replacing this test's own previous two-tier ("Starter"/"Growth") assertion, which
    // proved `0008`'s superseded grid.
    [Fact]
    public async Task HandleAsync_ListsExactlyOneTier_MatchingSubscriptionTierBands()
    {
        var handler = new GetPricingForOwnerHandler(new BillingOptions { BaseSeatPriceRub = 1m, PricePerExtraSeatRub = 1m });

        var response = await handler.HandleAsync(CancellationToken.None);

        var starter = Assert.Single(response.SeatPricing.Tiers);
        Assert.Equal(SubscriptionTierBands.Starter, starter.Key);
        Assert.Equal(SubscriptionTierBands.MinSeats, starter.MinSeats);
        Assert.Equal(SubscriptionTierBands.MaxSeats, starter.MaxSeats);

        // Every seat count SubscriptionTierBands.TryResolveTier actually resolves must fall inside
        // the one tier row this handler reports - the two sources must never disagree about where the
        // boundary sits.
        for (var seats = SubscriptionTierBands.MinSeats; seats <= SubscriptionTierBands.MaxSeats; seats++)
        {
            SubscriptionTierBands.TryResolveTier(seats, out var expectedTier);
            Assert.Equal(expectedTier, starter.Key);
        }
    }

    // `25-20`'s own honest finding, pinned at the unit level too (Ago.Chat.Integration.Tests.
    // OwnerPricingEndpointTests proves it end to end over real HTTP): no billing option carries a
    // price anywhere in this codebase today, so this handler never fabricates a list - see the
    // handler's own remarks for why this is not read from a configuration section.
    [Fact]
    public async Task HandleAsync_ReturnsNoBillingOptions_NoMechanismExistsToPriceThemYet()
    {
        var handler = new GetPricingForOwnerHandler(new BillingOptions { BaseSeatPriceRub = 1m, PricePerExtraSeatRub = 1m });

        var response = await handler.HandleAsync(CancellationToken.None);

        Assert.Empty(response.BillingOptions);
    }
}
