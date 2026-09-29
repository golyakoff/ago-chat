namespace Ago.Chat.Domain;

/// <summary>
/// `26-299`: the one proration rule every mid-cycle billing charge in this codebase now shares - Domain,
/// not Application, because "how many days of a period are still billable" is pure arithmetic over two
/// instants and a period length, no I/O, the identical placement <see cref="SubscriptionTierBands.ComputeSeatPriceRub"/>
/// already earns for the seat-band formula (CLAUDE.md rule 1: Domain references nothing). Before this
/// item, <c>ChangeSubscriptionSeatsHandler</c>/<c>PurchaseAdministratorSlotHandler</c>/
/// <c>PurchaseChannelAddOnHandler</c> each hand-rolled the identical <c>Math.Clamp((periodEnd - now).TotalDays, ...)</c>
/// expression inline - this type is that expression, named once, with one behavioural correction applied
/// everywhere at once.
///
/// <para><b>Floor rule, not the plain fractional remainder this codebase used before this item.</b> A
/// purchase made seconds after a period started used to prorate against the exact elapsed fraction of a
/// day, so buying something on the very first day of a period billed for slightly less than a full period
/// (e.g. 999.98 ₽ instead of 1000 ₽ on a 30-day, 1000 ₽/mo add-on bought 3 seconds after checkout
/// succeeded) - correct arithmetic, wrong customer experience: nobody reads "you get almost the whole
/// month" as "you were charged for the whole month". This type instead counts only <i>complete</i> elapsed
/// days (<see cref="Math.Floor(double)"/>) before subtracting them from the period length, so a purchase
/// on the day the period started - however many hours into that day - is always billed the full period,
/// and a purchase on day N (N complete days already elapsed) is billed for exactly
/// <c>periodLengthDays - N</c> days, matching what a person reading a day counter would expect.</para>
/// </summary>
public static class BillingProration
{
    /// <summary>How many whole days have elapsed since <paramref name="periodStart"/>, as of
    /// <paramref name="now"/> - never negative (a caller passing a <paramref name="now"/> at or before
    /// <paramref name="periodStart"/>, the ordinary case for "bought on day one", gets zero).</summary>
    public static int CompleteDaysElapsed(DateTimeOffset now, DateTimeOffset periodStart)
    {
        var elapsedDays = (now - periodStart).TotalDays;
        return elapsedDays <= 0 ? 0 : (int)Math.Floor(elapsedDays);
    }

    /// <summary>The fraction of <paramref name="periodLength"/> still billable, at this type's own floor
    /// rule - `1` for anything bought on the period's own first calendar day, falling in whole-day steps
    /// thereafter, floored at `0` once <paramref name="periodLength"/> itself has fully elapsed (never
    /// negative - a period cannot owe less than nothing).</summary>
    public static decimal BillableDaysFraction(DateTimeOffset now, DateTimeOffset periodStart, TimeSpan periodLength)
    {
        var periodLengthDays = (int)periodLength.TotalDays;
        var completeDaysElapsed = Math.Min(CompleteDaysElapsed(now, periodStart), periodLengthDays);
        var billableDays = periodLengthDays - completeDaysElapsed;
        return (decimal)billableDays / periodLengthDays;
    }

    /// <summary>The prorated Rouble amount for the remainder of a period, at this type's own floor rule.
    /// <paramref name="fullPeriodAmountRub"/> is whatever the full period would cost - a flat add-on's own
    /// price, or a seat-band delta the caller already computed (<c>newPrice - oldPrice</c>) - and may be
    /// negative (a downgrade's own delta, previewed but never actually charged by this codebase today).
    /// Rounded to two decimal places away from zero to match ЮKassa's own fixed-point amount field
    /// (`ChangeSubscriptionSeatsHandler`'s own rounding remarks before this item, unchanged in
    /// substance).</summary>
    public static decimal Prorate(decimal fullPeriodAmountRub, DateTimeOffset now, DateTimeOffset periodStart, TimeSpan periodLength) =>
        Math.Round(fullPeriodAmountRub * BillableDaysFraction(now, periodStart, periodLength), 2, MidpointRounding.AwayFromZero);
}
