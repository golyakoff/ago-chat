namespace Ago.Chat.Domain.Tests;

/// <summary>
/// `26-299`: proves the floor rule <see cref="BillingProration"/>'s own remarks describe - a purchase on
/// the period's own first calendar day is billed the full period, never a few seconds' worth short. The
/// first test below is the exact "fails-before" case this item's own report names: before this item, the
/// old <c>(periodEnd - now) / periodLengthDays</c> formula charged 999.98 ₽ instead of 1000 ₽ for a
/// purchase three seconds into a 30-day, 1000 ₽/mo period, because it prorated against the exact elapsed
/// fraction of a day rather than complete elapsed days.
/// </summary>
public class BillingProrationTests
{
    private static readonly TimeSpan PeriodLength = TimeSpan.FromDays(30);

    [Fact]
    public void Prorate_OnTheFirstDayOfThePeriod_ChargesTheFullAmount_EvenSecondsIn()
    {
        var periodStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = periodStart + TimeSpan.FromSeconds(3);

        var amount = BillingProration.Prorate(1000m, now, periodStart, PeriodLength);

        // Before this item's floor rule: 1000 * (30 - 3/86400) / 30 = 999.9998... rounded to 999.98/1000
        // depending on the exact seconds elapsed - always a few cents short, never the honest full price.
        Assert.Equal(1000m, amount);
    }

    [Fact]
    public void Prorate_OnTheFirstDayOfThePeriod_ChargesTheFullAmount_HoursIn()
    {
        var periodStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = periodStart + TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59);

        var amount = BillingProration.Prorate(1000m, now, periodStart, PeriodLength);

        Assert.Equal(1000m, amount);
    }

    [Fact]
    public void Prorate_OnDayTen_ChargesForTwentyOfThirtyDays()
    {
        var periodStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        // 10 complete days elapsed (day counter matches the console-mockup demo's own CFG.USED_DAYS = 10).
        var now = periodStart + TimeSpan.FromDays(10) + TimeSpan.FromHours(5);

        var amount = BillingProration.Prorate(1000m, now, periodStart, PeriodLength);

        Assert.Equal(666.67m, amount);
    }

    [Fact]
    public void Prorate_ExactlyOnADayBoundary_TreatsThatDayAsElapsed()
    {
        var periodStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = periodStart + TimeSpan.FromDays(15); // exactly 15.0 days elapsed

        var amount = BillingProration.Prorate(1000m, now, periodStart, PeriodLength);

        Assert.Equal(500m, amount);
    }

    [Fact]
    public void Prorate_AfterThePeriodHasFullyElapsed_ChargesNothing_NeverNegative()
    {
        var periodStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = periodStart + TimeSpan.FromDays(45); // well past the 30-day period

        var amount = BillingProration.Prorate(1000m, now, periodStart, PeriodLength);

        Assert.Equal(0m, amount);
    }

    [Fact]
    public void Prorate_WhenNowIsBeforePeriodStart_TreatsItAsZeroElapsed()
    {
        var periodStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = periodStart - TimeSpan.FromMinutes(5);

        var amount = BillingProration.Prorate(1000m, now, periodStart, PeriodLength);

        Assert.Equal(1000m, amount);
    }

    [Fact]
    public void Prorate_WithANegativeFullAmount_ProratesTheNegativeTheIdenticalWay()
    {
        // A downgrade's own delta - PreviewBillingPurchaseHandler may compute one; nothing in this
        // codebase charges it today, but the arithmetic must still behave symmetrically.
        var periodStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = periodStart + TimeSpan.FromDays(10);

        var amount = BillingProration.Prorate(-300m, now, periodStart, PeriodLength);

        Assert.Equal(-300m * 20 / 30, amount);
    }

    [Fact]
    public void CompleteDaysElapsed_FloorsPartialDays()
    {
        var periodStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = periodStart + TimeSpan.FromDays(10) + TimeSpan.FromHours(23);

        Assert.Equal(10, BillingProration.CompleteDaysElapsed(now, periodStart));
    }
}
