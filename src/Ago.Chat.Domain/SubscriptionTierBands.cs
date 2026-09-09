namespace Ago.Chat.Domain;

/// <summary>
/// `13-02`: the seat-count -> tier lookup, kept as pure Domain logic (no I/O, no options dependency)
/// so `CreateCheckoutSessionHandler` can validate a requested seat count before ever calling out to
/// ЮKassa, and so a unit test can assert the boundary without a database or a fake HTTP host.
///
/// <para><b>`25-29`: the band shape below is `ago-business` decision `0012` (2026-09-07), read
/// directly, not `0008`'s superseded grid `13-02`/`13-08` originally implemented.</b> `0012`'s own
/// "## Business" heading states the range in so many words - "2–5 операторских мест" - and its own
/// table gives four exact prices: 2 and 3 seats both 490 ₽, 4 seats 690 ₽, 5 seats 890 ₽. That is a
/// flat base charge (<see cref="BaseSeats"/> seats or fewer) plus a per-seat charge only past the
/// base (<see cref="ComputeSeatPriceRub"/>) - not the flat `price × seats` `0008` priced and this type
/// implemented until this item. `0008`'s own "Custom" tier (6+ seats, sold only through a
/// conversation, no self-serve price) survives unchanged in spirit: `0012`'s own §1c calls a 6-10
/// "Premium" band designed but "намеренно не выпущен" (deliberately not released) pending a real
/// customer at that scale, and nothing above 10 is decided at all - so, as before, this type invents
/// no self-serve band past what `0012` actually prices.</para>
///
/// <para><b>`0012` deliberately reopens the overlap `13-08` closed.</b> Before this item, 1-2 seats
/// were <see cref="Site.SeatLimit"/>'s own free-tier allowance and never a purchasable count -
/// `13-08`'s own resolution to "raising the free ceiling to two without also moving `MinSeats` would
/// let a site 'buy' the seats it already has for free." `0012`'s own text answers that directly:
/// "Business на двух местах покупают не за места" (Business at two seats is not bought for the
/// seats) - a buyer at the free tier's own seat count is paying for what free never gives: permanent
/// history instead of a retention window, a second Administrator, and no auto-deletion for
/// inactivity. `0012` treats that overlap as the intended shape of the product, not as a defect this
/// type exists to prevent, so `MinSeats` moves down to <b>2</b> and <see cref="FreeSeatsIncluded"/>
/// stops being derived from it (see that constant's own remarks).</para>
///
/// <para><b>The historical band-boundary reading below is kept for context, not because it still
/// governs anything.</b> `roadmap.md`'s Stage 13 planning named the bands as "Starter (2-10 seats),
/// Growth (10-100 seats)" - literally overlapping at 10. `13-02` read that as Starter = 2-9,
/// Growth = 10-100; `13-08` then moved Starter's own floor from 2 to 3 when the free tier's own
/// ceiling grew to two seats, giving Starter = 3-9, Growth = 10-100. `0012` supersedes both readings:
/// there is no "Growth" band in the current pricing at all, and <see cref="MaxSeats"/> caps the one
/// real band at 5. <see cref="Growth"/>/<see cref="GrowthMinSeats"/> stay defined only because
/// <see cref="RetentionClass"/> still spells a historical or over-limit site's retention policy by
/// that name - <see cref="TryResolveTier"/> can no longer resolve any seat count to it, since
/// <see cref="MaxSeats"/> now sits below <see cref="GrowthMinSeats"/>. Correcting the "growth"
/// tier-name/retention-policy question itself is `0012`'s own storage sections (§5/§6), a different
/// item's scope, not this one's.</para>
/// </summary>
public static class SubscriptionTierBands
{
    public const string Starter = "starter";

    /// <summary>`0012` supersedes the "Growth" band this string used to name (see this type's own
    /// remarks) - kept defined only so <see cref="RetentionClass"/> and any site already on this tier
    /// from before `0012` keep resolving a retention policy by name. No seat count resolves to it
    /// through <see cref="TryResolveTier"/> any more.</summary>
    public const string Growth = "growth";

    /// <summary>`25-29`: lowered from `3` to `2` - `ago-business` decision `0012`'s own "2–5
    /// операторских мест" states the paid band's floor directly, and its price table charges a real,
    /// non-zero price (490 ₽) at exactly 2 seats. This deliberately re-opens the numeric overlap with
    /// <see cref="Site.SeatLimit"/>'s free-tier default (also 2) that `13-08` once closed - see this
    /// type's own remarks for why `0012` treats that overlap as the point, not a defect.</summary>
    public const int MinSeats = 2;

    /// <summary>`25-29`: lowered from `100` to `5` - `0012`'s own price table stops at 5 seats, and
    /// its own §1c states plainly that the next band (6-10, "Premium") is designed but deliberately
    /// unreleased, with nothing at all decided above 10. `CLAUDE.md`'s "do not invent... typical
    /// production figures" applies the same way it did when this type first drew a line at 100 with
    /// nothing in the pricing data to justify going further - the honest cap is wherever `0012`'s own
    /// numbers stop, which is now 5, not 100.</summary>
    public const int MaxSeats = 5;

    /// <summary>`25-29`: how many seats <see cref="Application.UseCases.CreateCheckoutSession.BillingOptions.BaseSeatPriceRub"/>
    /// alone covers - `0012`'s own price table charges the identical 490 ₽ at both 2 and 3 seats, then
    /// a strictly higher price at 4 and 5. <see cref="ComputeSeatPriceRub"/> is the one place this
    /// constant is read; a seat count at or below it contributes nothing past the base charge, and
    /// every seat past it contributes <see cref="Application.UseCases.CreateCheckoutSession.BillingOptions.PricePerExtraSeatRub"/>
    /// once each.</summary>
    public const int BaseSeats = 3;

    /// <summary>`25-20`: made `public` (was `private`) so a reader of the band boundaries could list
    /// them without a second, hand-typed literal drifting from this one. `25-29`: no longer reachable
    /// through <see cref="TryResolveTier"/> at all (see this type's own remarks) - kept only for
    /// <see cref="RetentionClass"/>'s sake, so that consumer's own "growth" retention window stays
    /// anchored to a number, however historical.</summary>
    public const int GrowthMinSeats = 10;

    /// <summary>`25-29`: the free tier's own seat ceiling - `ago-business` decision `0012`'s own
    /// "Solo" row, "Два операторских аккаунта." <b>No longer derived as <c>MinSeats - 1</c>.</b> That
    /// derivation encoded `13-08`'s own invariant - the free ceiling and the paid floor are adjacent,
    /// non-overlapping numbers - which `0012` deliberately abandons (this type's own remarks: Business
    /// is now buyable at exactly the free tier's own seat count, for reasons that have nothing to do
    /// with seats). The two constants now happen to share the same value (2) because `0012` says so,
    /// not because one is computed from the other; a future change to either number on its own,
    /// without checking the other against `0012` again, is exactly the drift this comment exists to
    /// stop from looking like a harmless refactor.</summary>
    public const int FreeSeatsIncluded = 2;

    /// <summary>
    /// <see langword="true"/> and the resolved tier name for any seat count in [<see cref="MinSeats"/>,
    /// <see cref="MaxSeats"/>]; <see langword="false"/> (and an empty <paramref name="tier"/>) for
    /// anything outside that range. `25-29`: unlike before this item, the free tier's own seat count
    /// (2) now resolves successfully too - see this type's own remarks on why `0012` intends that.
    /// </summary>
    public static bool TryResolveTier(int requestedSeats, out string tier)
    {
        if (requestedSeats < MinSeats || requestedSeats > MaxSeats)
        {
            tier = string.Empty;
            return false;
        }

        // `25-29`: the `>= GrowthMinSeats` branch is now structurally unreachable - `MaxSeats` (5)
        // sits below `GrowthMinSeats` (10), so no value that survives the range check above can ever
        // satisfy it. Left in this shape rather than deleted: it costs nothing to keep, and rewriting
        // it as a bare `return Starter` would erase the one place a reader can see, side by side, that
        // `0012` removed the Growth band rather than this method quietly losing the ability to resolve
        // it by accident.
        tier = requestedSeats >= GrowthMinSeats ? Growth : Starter;
        return true;
    }

    /// <summary>
    /// `25-29`: `ago-business` decision `0012`'s own Business price table, as a formula rather than a
    /// hand-typed lookup for each of the four seat counts it happens to enumerate (2/3/4/5) - the same
    /// "read the rule the table is an instance of" choice this codebase already makes for banded
    /// pricing elsewhere. Seats at or below <see cref="BaseSeats"/> cost exactly
    /// <paramref name="baseSeatPriceRub"/>; every seat past it adds <paramref name="pricePerExtraSeatRub"/>
    /// once. Checked against `0012`'s own four rows: 2 and 3 seats -&gt; 490 + 0 = 490; 4 seats -&gt;
    /// 490 + 1×200 = 690; 5 seats -&gt; 490 + 2×200 = 890.
    ///
    /// <para><b>Pure Domain logic, not a `BillingOptions` method.</b> The band <em>shape</em> - which
    /// seat counts share the base price, and that every seat past it costs the same fixed amount - is
    /// a business rule `ago-business` owns and this type already exists to express (`TryResolveTier`'s
    /// own identical role for the tier boundary). The Rouble <em>amounts</em> are configuration
    /// (`BillingOptions`'s own "measure or stay silent" rule - `CLAUDE.md`), which is why they arrive
    /// as parameters rather than living here as a second pair of constants: this method would be
    /// exactly as correct, and exactly as untestable without a live deployment, if it hardcoded 490
    /// and 200 the way <see cref="BaseSeats"/> hardcodes 3 - the difference is that a wrong seat-band
    /// boundary is a logic bug this project can catch in a unit test, while a wrong Rouble figure is a
    /// number nobody here is positioned to invent (the identical distinction
    /// `BillingOptions.BaseSeatPriceRub`'s own remarks draw for itself).</para>
    ///
    /// <para>Not range-checked against <see cref="MinSeats"/>/<see cref="MaxSeats"/> here - every
    /// caller already resolves a tier through <see cref="TryResolveTier"/> first and only reaches this
    /// method once that succeeds, the same "validate once, at the boundary" shape
    /// `CreateCheckoutSessionHandler`'s own call order already establishes.</para>
    /// </summary>
    public static decimal ComputeSeatPriceRub(int seats, decimal baseSeatPriceRub, decimal pricePerExtraSeatRub)
    {
        var extraSeats = Math.Max(0, seats - BaseSeats);
        return baseSeatPriceRub + (extraSeats * pricePerExtraSeatRub);
    }

    /// <summary>`25-25`: the free tier's own Administrator-seat ceiling - `ago-business` decision
    /// `0012`'s "Solo" row, "один администратор." Named the same way <see cref="FreeSeatsIncluded"/>
    /// already is, rather than a bare literal `1` repeated at every call site.</summary>
    public const int FreeAdminsIncluded = 1;

    /// <summary>`25-25`: every paid band's own Administrator-seat ceiling - `0012`'s "Business" row,
    /// "До 2 администраторов включено... Основной и заместитель." One number for both
    /// <see cref="Starter"/> and <see cref="Growth"/>: `0012` prices Administrators against the tier as
    /// a whole ("Business"), never against the seat count that happens to split it into two bands here
    /// - the seat-count split exists only because `13-02` needed one for <em>seats</em>, and nothing in
    /// `0011`/`0012` gives Administrators a different ceiling on one side of it than the other.</summary>
    public const int BusinessAdminsIncluded = 2;

    /// <summary>
    /// `25-25`: the Administrator-seat entitlement for <paramref name="tier"/> - a pure function of the
    /// tier alone, unlike <see cref="Site.SeatLimit"/>'s own seat count, which the buyer chooses at
    /// checkout and <see cref="TryResolveTier"/> only ever resolves the other direction (seats -> tier).
    /// `CreateCheckoutSessionHandler` never asks "how many administrators" - `0012`'s own grid gives
    /// every paid tier the identical included count regardless of how many seats were bought, so there
    /// is nothing for a buyer to choose here and nothing for this method to take beyond the tier name
    /// already in hand.
    ///
    /// <para><b>`25-29` correction: a priced third administrator is real in `0012`, only unbuilt here.</b>
    /// This paragraph used to read "`0012` calls a third administrator on a paid tier 'кастом' (custom)
    /// with no number attached" - read directly against `0012` while investigating `25-29`, that is
    /// false: `0012`'s own "## Business" section states a real price, "+500 ₽/мес за каждого [администратора]
    /// сверх двух" (+500 ₽/mo for each administrator beyond two). The "custom, no public price" posture
    /// belongs to `0008`'s seat table past five seats (and `0012`'s own unreleased 6-10 "Premium" seat
    /// band), never to a third Administrator - conflating the two was this comment's own error, not a
    /// fact about the pricing document. <b>The real gap `25-29` found and did not close:</b> the
    /// Administrator-promotion guard (`ChangeOperatorRoleHandler`, `25-25`) refuses a promotion past
    /// <see cref="BusinessAdminsIncluded"/> outright - there is no purchase path that raises this
    /// ceiling the way `ChangeSubscriptionSeatsHandler` raises <see cref="Site.SeatLimit"/>, so no site
    /// can ever legally hold a third Administrator today and there is nothing yet for a +500 ₽ charge
    /// to attach to. Charging it for real needs a purchasable "how many extra Administrators" count
    /// persisted somewhere (mirroring `BillingSubscription.RequestedSeats`) and a matching change to
    /// that handler's own guard - new persisted state, and therefore a migration, which `25-29`'s own
    /// report defers to the next available migration slot rather than building here.</para>
    ///
    /// <para><b>"free" is the one tier name this file does not itself define a constant for.</b>
    /// <see cref="Site.Tier"/>'s own default and every other free-tier check in this codebase
    /// (`Stage13RaiseFreeTierSeatLimit`'s own `WHERE tier = 'free'`) already spell it the same bare way,
    /// so this method matches that existing convention rather than introducing a new named constant
    /// nothing else would share.</para>
    /// </summary>
    public static int ResolveAdminLimit(string tier) => tier == "free" ? FreeAdminsIncluded : BusinessAdminsIncluded;
}
