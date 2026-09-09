namespace Ago.Chat.Domain;

/// <summary>
/// `13-02`: the seat-count -> tier lookup, kept as pure Domain logic (no I/O, no options dependency)
/// so `CreateCheckoutSessionHandler` can validate a requested seat count before ever calling out to
/// ЮKassa, and so a unit test can assert the boundary without a database or a fake HTTP host.
///
/// <para><b>The band-boundary reading, stated here because the backlog's own source data is ambiguous.</b>
/// `roadmap.md`'s Stage 13 planning names the bands as "Starter (2-10 seats), Growth (10-100 seats)" -
/// literally overlapping at 10. `13-02` originally read that as the natural non-overlapping split a
/// loosely worded band list almost always means: Starter = 2-9 seats, Growth = 10-100 seats, i.e. a
/// requested seat count belongs to Growth from the point it first reaches the number named as Growth's
/// own lower bound. `13-08` (below) moved Starter's own floor from 2 to 3 for an unrelated, later
/// reason - the free tier's ceiling growing to two seats, not a correction to this reading - so the
/// bands as they stand today are <b>Starter = 3-9 seats, Growth = 10-100 seats</b>. This paragraph
/// records the original reading as history; <see cref="MinSeats"/>/<see cref="GrowthMinSeats"/> are the
/// live values. If ago-business's real pricing meant something else than either reading, this is still
/// the one method to correct.</para>
///
/// <para><b>`13-08`: 1 and 2 seats have no band at all - both are the free tier every site starts on
/// (<see cref="Site.SeatLimit"/>'s own default), never something a checkout purchases.</b> Before
/// `13-08` the free tier held one seat and paid bands started at two, so the two ranges already touched
/// with no gap; raising the free ceiling to two without also moving <see cref="MinSeats"/> would have
/// made them overlap - a site could "buy" the exact seat count it already has for free. `13-08`'s own
/// resolution: paid bands start at <b>3</b>, the first seat count that actually adds capacity beyond
/// what free already gives - so the free tier's ceiling and the paid ladder's floor stay two distinct,
/// non-overlapping meanings ("what free includes" vs. "what the cheapest paid band adds") that happen
/// to sit next to each other, not one boundary read two ways. 0 or negative is simply invalid input.
/// Above 100 has no band either: nothing in the given pricing data describes an Enterprise tier or an
/// uncapped seat count, so this item does not invent one - a seat count outside [<see cref="MinSeats"/>,
/// <see cref="MaxSeats"/>] is rejected by <see cref="TryResolveTier"/> the same way an out-of-range
/// value would be by a value object's own constructor, just expressed as a lookup miss instead of a
/// thrown exception, since the caller (<c>CreateCheckoutSessionHandler</c>) needs a <c>Result</c>
/// failure, not an unhandled exception, for an ordinary bad-input case a real client will trigger by
/// mistake.</para>
/// </summary>
public static class SubscriptionTierBands
{
    public const string Starter = "starter";

    public const string Growth = "growth";

    /// <summary>`13-08`: raised from `2` to `3` when the free tier's own ceiling moved from one seat to
    /// two - see this type's own remarks for why the two numbers must move together rather than being
    /// left to overlap.</summary>
    public const int MinSeats = 3;

    public const int MaxSeats = 100;

    /// <summary>`25-20`: made `public` (was `private`) so a reader of the band boundaries - the owner's
    /// own price-list screen (<c>GetPricingForOwnerHandler</c>) is the first - can list Starter's own
    /// upper bound and Growth's own lower one without a second, hand-typed `10` drifting from this one.
    /// No behavioural change: still the identical compile-time constant <see cref="TryResolveTier"/>
    /// already compared against.</summary>
    public const int GrowthMinSeats = 10;

    /// <summary>`25-20`: the free tier's own seat ceiling, restated as a named constant rather than a
    /// second literal `2` - this type's own remarks above already establish the fact
    /// (<see cref="MinSeats"/> `- 1`is deliberate, not derived after the fact: "paid bands start at
    /// the first seat count that actually adds capacity beyond what free already gives"), so a reader
    /// needing "how many seats does the free tier include" (the owner's own price-list screen) gets it
    /// from the identical source <see cref="MinSeats"/> itself came from, rather than a duplicate of
    /// <see cref="Site.SeatLimit"/>'s own default that could drift from either.</summary>
    public const int FreeSeatsIncluded = MinSeats - 1;

    /// <summary>
    /// <see langword="true"/> and the resolved tier name for any seat count in [<see cref="MinSeats"/>,
    /// <see cref="MaxSeats"/>]; <see langword="false"/> (and an empty <paramref name="tier"/>) for
    /// anything outside that range, including the free tier's own two seats (`13-08`).
    /// </summary>
    public static bool TryResolveTier(int requestedSeats, out string tier)
    {
        if (requestedSeats < MinSeats || requestedSeats > MaxSeats)
        {
            tier = string.Empty;
            return false;
        }

        tier = requestedSeats >= GrowthMinSeats ? Growth : Starter;
        return true;
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
    /// <para><b>No ceiling above <see cref="BusinessAdminsIncluded"/> exists yet.</b> `0012` calls a
    /// third administrator on a paid tier "кастом" (custom) with no number attached - the same posture
    /// `0008`'s seat-price table already takes for seats past five - so this method does not invent an
    /// upper bound past the included count; a site that already holds more than the included number
    /// (there is no purchase path for a further one today) is a real, allowed, over-limit state exactly
    /// like `GetSeatAssignmentSummaryHandler`'s own `OverSeats` condition for seats - read-time-derived,
    /// never blocked retroactively by a downgrade or a tier recompute.</para>
    ///
    /// <para><b>"free" is the one tier name this file does not itself define a constant for.</b>
    /// <see cref="Site.Tier"/>'s own default and every other free-tier check in this codebase
    /// (`Stage13RaiseFreeTierSeatLimit`'s own `WHERE tier = 'free'`) already spell it the same bare way,
    /// so this method matches that existing convention rather than introducing a new named constant
    /// nothing else would share.</para>
    /// </summary>
    public static int ResolveAdminLimit(string tier) => tier == "free" ? FreeAdminsIncluded : BusinessAdminsIncluded;
}
