using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetPricingForOwner;

/// <summary>
/// `25-20`: the platform owner's own price-list read - every currently-paid capability's price, read
/// from the identical configuration the billing code itself charges from
/// (<see cref="Abstractions.IPriceCatalogRepository"/>, <see cref="SubscriptionTierBands"/>,
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
/// <para><b>`25-43`: reads the two seat-pricing keys' own currently-effective versions through
/// <see cref="Abstractions.IPriceCatalogRepository"/> instead of a compile-time
/// `BillingOptions`.</b> The wire shape is unchanged - `api-design.md`'s "never remove a field" rule,
/// and `25-42`'s own unstarted item is expected to read these exact fields - only the source moved.
/// Neither key having no published version at all is treated as unreachable here: this screen exists
/// to show the owner what a real charge would actually use, and if it can be reached with either
/// unpublished, `25-29`/`25-43`'s own migration seed (which publishes both the day this mechanism
/// ships) never ran, or was rolled back - a deployment state genuinely worth a loud failure rather than
/// a screen quietly showing a fabricated zero. `PricedResourceKeys.All`, not just these two, drives the
/// row list below - `25-43`'s own second decision ("a key with no price at all is the ordinary
/// 'built, not yet for sale' state") is why a third key with nothing published yet still appears here,
/// honestly, rather than being hidden until it has a real number.</para>
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
public sealed class GetPricingForOwnerHandler(IPriceCatalogRepository prices)
{
    public async Task<OwnerPricingResponse> HandleAsync(CancellationToken cancellationToken)
    {
        // `25-29`: one row, not two - `ago-business` decision `0012` prices exactly one Business band
        // (`SubscriptionTierBands.MinSeats`-`SubscriptionTierBands.MaxSeats`, 2-5 seats today), never a
        // "Growth" band the way `0008`'s superseded grid did. `SubscriptionTierBands.Growth` stays
        // defined for `RetentionClass`'s own sake (that type's own remarks), but no purchasable seat
        // count resolves to it any more, so this list does not invent a row for it.
        var tiers = new List<OwnerSeatTierDto>
        {
            new(SubscriptionTierBands.Starter, SubscriptionTierBands.MinSeats, SubscriptionTierBands.MaxSeats),
        };

        var basePrice = await prices.FindCurrentAsync(SubscriptionTierBands.BaseSeatPriceKey, cancellationToken);
        var extraPrice = await prices.FindCurrentAsync(SubscriptionTierBands.ExtraSeatPriceKey, cancellationToken);
        if (basePrice is null || extraPrice is null)
        {
            // `25-43`: unreachable on a deployment whose migration seed ran - see this handler's own
            // remarks. Thrown, not translated into an empty/zeroed response: a platform owner reading
            // this screen must never be shown a fabricated number for a key this codebase already
            // charges real money against.
            throw new InvalidOperationException(
                "The owner price list was read but the seat-pricing keys have no published version - "
                + "the migration seed that publishes them on this mechanism's first deploy did not run.");
        }

        var seatPricing = new OwnerSeatPricingDto(
            // `25-29`/`25-43`: kept on the wire, but no longer "the" seat price - see this field's own
            // remarks on `OwnerSeatPricingDto` for why the marginal rate is what is reported here.
            extraPrice.AmountRub,
            SubscriptionTierBands.BaseSeats,
            basePrice.AmountRub,
            extraPrice.AmountRub,
            BillingSubscription.PeriodLength.TotalDays,
            SubscriptionTierBands.FreeSeatsIncluded,
            tiers);

        // `25-43`: every registered key, not only the two seat-pricing ones - the second decision's
        // own "built, not yet for sale is the ordinary state" made visible: a third key with nothing
        // published yet still appears here, with a null amount, rather than being hidden until priced.
        var pricedResources = new List<OwnerPricedResourceDto>();
        foreach (var descriptor in PricedResourceKeys.All)
        {
            var current = await prices.FindCurrentAsync(descriptor.Key, cancellationToken);
            pricedResources.Add(new OwnerPricedResourceDto(descriptor.Key.Value, descriptor.Label, current?.Version, current?.AmountRub));
        }

        // See this handler's own remarks above for why this is always `[]` today, on every
        // deployment, rather than read from a configuration section.
        var declaredBillingOptions = Array.Empty<OwnerBillingOptionDto>();

        return new OwnerPricingResponse(seatPricing, declaredBillingOptions, pricedResources);
    }
}
