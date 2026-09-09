using Ago.Chat.Application.UseCases.CreateCheckoutSession;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetPricingForOwner;

/// <summary>
/// `25-20`: the platform owner's own price-list read - every currently-paid capability's price, read
/// from the identical configuration the billing code itself charges from
/// (<see cref="BillingOptions"/>, <see cref="SubscriptionTierBands"/>,
/// <see cref="BillingSubscription.PeriodLength"/>), never retyped by hand from the private
/// `ago-business` repository's own decision documents.
///
/// <para><b>No command record, unlike every other handler in this folder.</b> This read takes no
/// caller input at all - unlike <see cref="ListSitesForOwner.ListSitesForOwner"/>'s optional
/// search/page fields, there is nothing here for a caller to name (one deployment has exactly one
/// price list), so a record with zero properties would exist only to satisfy a convention nothing else
/// needs - `clean-architecture.md`'s own qualifying rules ask for the structure a real need creates,
/// not the structure a pattern suggests.</para>
///
/// <para><b>This handler performs no authorization check, and that is deliberate</b> - the identical
/// reasoning <see cref="ListSitesForOwner.ListSitesForOwnerHandler"/>'s own remarks give for itself:
/// the fact that authorizes this call is the `RequirePlatformOwner` policy on the one route that
/// resolves this handler (<c>OwnerPricingEndpoints</c>), a Keycloak realm role Application has no port
/// to see and should not re-check with a weaker, second copy of the same decision.</para>
///
/// <para><b><see cref="OwnerPricingResponse.BillingOptions"/> is hardcoded empty here, not read from
/// any configuration section.</b> The honest finding this item's own report states in full: no
/// `BillingOptionEntitlements:*` key is configured on this deployment (`ago-deploy`'s own manifests
/// carry none), and even a deployment that configured one would only be declaring *what module it
/// turns on* - <see cref="Abstractions.IBillingOptionEntitlementProvider"/>'s own remarks are explicit
/// that no configuration key for a billing option's own *price* exists anywhere in this codebase yet.
/// Enumerating today's zero declared keys would need a new Application port this item does not
/// otherwise need (the existing port resolves one named key, never lists them) purely to prove a list
/// is empty - the same "small, stated interim answer, not a silent shortcut" judgement `25-19`'s own
/// report makes for its own dropdown, applied here to a section with nothing behind it to enumerate at
/// all. The field stays on the wire (typed, never omitted) so a real mechanism can start filling it
/// without a breaking contract change later.</para>
/// </summary>
public sealed class GetPricingForOwnerHandler(BillingOptions billingOptions)
{
    public Task<OwnerPricingResponse> HandleAsync(CancellationToken cancellationToken)
    {
        var tiers = new List<OwnerSeatTierDto>
        {
            new(SubscriptionTierBands.Starter, SubscriptionTierBands.MinSeats, SubscriptionTierBands.GrowthMinSeats - 1),
            new(SubscriptionTierBands.Growth, SubscriptionTierBands.GrowthMinSeats, SubscriptionTierBands.MaxSeats),
        };

        var seatPricing = new OwnerSeatPricingDto(
            billingOptions.PricePerSeatRub,
            BillingSubscription.PeriodLength.TotalDays,
            SubscriptionTierBands.FreeSeatsIncluded,
            tiers);

        // See this handler's own remarks above for why this is always `[]` today, on every
        // deployment, rather than read from a configuration section.
        var declaredBillingOptions = Array.Empty<OwnerBillingOptionDto>();

        return Task.FromResult(new OwnerPricingResponse(seatPricing, declaredBillingOptions));
    }
}
