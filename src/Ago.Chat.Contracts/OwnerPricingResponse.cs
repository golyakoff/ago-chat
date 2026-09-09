namespace Ago.Chat.Contracts;

/// <summary>
/// `25-20`: `GET /api/v1/owner/pricing`'s response body - every currently-paid capability's price,
/// read from the same configuration the billing code itself charges from, never retyped by hand from
/// `ago-business`'s own decision documents (this item's own Scope: "not retyped from the `ago-business`
/// document by hand, which would drift from the code's own numbers the first time either changes").
///
/// <para><b>Read-only, and this is the only response this endpoint ever returns.</b> There is no
/// corresponding `PUT`/`PATCH` - `23-86`'s own Scope already drew this line for the mechanism this
/// screen reads ("the deployment declares what an option turns on, never what it costs"), and this
/// item's own Scope repeats it for the screen itself: "this item does not build editing or a write
/// path."</para>
///
/// <para><b><see cref="BillingOptions"/> is honestly empty on every deployment today, and that is a
/// finding, not a bug in this response.</b> `IBillingOptionEntitlementProvider`'s own remarks: "the
/// deployment declares what an option turns on, never what it costs" - so even a deployment that has
/// configured a channel/AI/storage add-on's own <c>BillingOptionEntitlements:&lt;key&gt;</c> mapping
/// has configured *what it turns on*, never a number this response could show. `25-20`'s own report
/// says this in full; this field exists so the day a real price mechanism is added for those
/// categories, this contract grows an actual list rather than needing a new field
/// (`api-design.md`'s "add within a version, never remove or rename").</para>
/// </summary>
/// <param name="SeatPricing">The one pricing mechanism with real, deployed numbers behind it today -
/// see <see cref="OwnerSeatPricingDto"/>'s own remarks.</param>
/// <param name="BillingOptions">Every `BillingOptionEntitlements:*` key this deployment has declared,
/// each carrying only what <see cref="Application.Abstractions.IBillingOptionEntitlementProvider"/>
/// itself resolves (which module the option turns on) - never a price, because none exists to read.
/// Empty on this deployment (and on `ago-chat`'s own repository default), which this response states
/// rather than hides: an empty list here means "this deployment has declared no billing options",
/// not "the read failed".</param>
public sealed record OwnerPricingResponse(
    OwnerSeatPricingDto SeatPricing, IReadOnlyList<OwnerBillingOptionDto> BillingOptions);

/// <summary>
/// `13-02`/`13-08`: the per-seat subscription price and the seat bands it applies to - the only
/// capability in this codebase's billing surface with a real, currently-charged number behind it.
///
/// <para><b>`25-29`: the pricing this type describes stopped being flat, and this record grew to say
/// so.</b> `ago-business` decision `0012` (2026-09-07, read directly) charges a flat base for the
/// first few seats and a separate marginal rate past them - `BaseSeats`/`BaseSeatPriceRub`/
/// `PricePerExtraSeatRub` are the three new fields that state that formula completely.
/// <see cref="PricePerSeatRub"/> stays on the wire (`api-design.md`'s "add within a version, never
/// remove or rename" - removing it outright would silently crash `ago-console`'s already-shipped
/// `OwnerPricingPage.tsx`, which reads it with no null-guard), but it can no longer mean "the flat
/// price every seat costs" - see its own remarks for what it holds instead and why that is still
/// honest, not invented.</para>
/// </summary>
/// <param name="PricePerSeatRub">`25-29`: kept for wire compatibility, populated with
/// <see cref="PricePerExtraSeatRub"/>'s own value rather than removed - the marginal rate is at least
/// a real number this formula produces, unlike the flat rate this field used to hold, which no longer
/// exists to report. <b>This is not the price of every seat any more</b> - a seat at or below
/// <see cref="BaseSeats"/> costs a share of <see cref="BaseSeatPriceRub"/>, not this figure. Reading
/// this field alone (the way `OwnerPricingPage.tsx`'s own "Price per seat" column does today) under-
/// or over-states the true charge for any seat count that is not exactly one past the base - `25-29`'s
/// own report flags this as a needed console follow-up rather than treating it as closed by this
/// field's mere presence on the wire.</param>
/// <param name="BaseSeats">`25-29`: <see cref="Domain.SubscriptionTierBands.BaseSeats"/> - how many
/// seats <see cref="BaseSeatPriceRub"/> alone covers.</param>
/// <param name="BaseSeatPriceRub">`25-29`: `BillingOptions.BaseSeatPriceRub` - the flat charge for
/// <see cref="BaseSeats"/> seats or fewer.</param>
/// <param name="PricePerExtraSeatRub">`25-29`: `BillingOptions.PricePerExtraSeatRub` - the marginal
/// charge added once for every seat past <see cref="BaseSeats"/>.</param>
/// <param name="BillingPeriodDays"><see cref="Domain.BillingSubscription.PeriodLength"/>'s own
/// `TotalDays` - how often this pricing is charged, read from the identical constant the renewal job
/// itself uses, never a second `30` typed here.</param>
/// <param name="FreeSeatsIncluded"><see cref="Domain.SubscriptionTierBands.FreeSeatsIncluded"/> -
/// how many seats every site starts on before any purchase, at no charge.</param>
/// <param name="Tiers">The named seat bands a purchase resolves to, in ascending order - see
/// <see cref="OwnerSeatTierDto"/>'s own remarks.</param>
public sealed record OwnerSeatPricingDto(
    decimal PricePerSeatRub,
    int BaseSeats,
    decimal BaseSeatPriceRub,
    decimal PricePerExtraSeatRub,
    double BillingPeriodDays,
    int FreeSeatsIncluded,
    IReadOnlyList<OwnerSeatTierDto> Tiers);

/// <summary>One named seat band from <see cref="Domain.SubscriptionTierBands"/> - <see cref="MinSeats"/>
/// and <see cref="MaxSeats"/> are inclusive, matching <see cref="Domain.SubscriptionTierBands.TryResolveTier"/>'s
/// own range check. `25-29`: there is exactly one row today - `ago-business` decision `0012` prices one
/// Business band (2-5 seats), not two - carried as a list rather than narrowed to a single record
/// because nothing about this response's own shape assumes exactly one, and a future band (`0012`'s own
/// designed-but-unreleased "Premium", 6-10 seats) can start appearing here the day it is priced, with
/// no contract change.</summary>
public sealed record OwnerSeatTierDto(string Key, int MinSeats, int MaxSeats);

/// <summary>One `BillingOptionEntitlements:*` key this deployment has declared - see
/// <see cref="OwnerPricingResponse"/>'s own remarks for why <see cref="PriceRub"/> is always
/// <see langword="null"/> today, on every deployment, and why that is a faithful read rather than a
/// gap in this endpoint.</summary>
/// <param name="OptionKey">The billing catalog's own opaque SKU
/// (<see cref="Domain.BillingOptionKey"/>) - `ago-business`'s own vocabulary, never a literal this
/// assembly names (`BillingOptionKey`'s own remarks).</param>
/// <param name="ModuleKey">What buying <paramref name="OptionKey"/> turns on, resolved through
/// <see cref="Application.Abstractions.IBillingOptionEntitlementProvider"/> - the identical port
/// <see cref="Infrastructure.Postgres.SubscriptionRenewalApplier"/> already resolves at renewal
/// time.</param>
/// <param name="PriceRub"><see langword="null"/> always, on this deployment and on every deployment
/// this codebase can describe today - no configuration key for a billing option's own price exists
/// anywhere in this codebase (`IBillingOptionEntitlementProvider`'s own remarks: "carries no price...
/// prices live in the private `ago-business` repository"). Present on the wire now, rather than
/// omitted, for the identical forward-compatibility reason `OwnerSiteSummaryDto.Tier`'s own remarks
/// give: the day a real price mechanism exists for these options, this field starts reading one
/// without a breaking contract change.</param>
public sealed record OwnerBillingOptionDto(string OptionKey, string? ModuleKey, decimal? PriceRub);
