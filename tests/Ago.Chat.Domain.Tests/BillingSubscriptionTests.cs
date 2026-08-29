namespace Ago.Chat.Domain.Tests;

public class BillingSubscriptionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_WhenValid_StartsPending()
    {
        var subscription = BillingSubscription.Create(
            new BillingSubscriptionId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), "pmt_123", 5, SubscriptionTierBands.Starter, Now);

        Assert.Equal(BillingSubscriptionStatus.Pending, subscription.Status);
        Assert.Null(subscription.PaymentMethodId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WhenPaymentIdIsEmptyOrWhitespace_Throws(string paymentId)
    {
        Assert.Throws<ArgumentException>(() => BillingSubscription.Create(
            new BillingSubscriptionId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), paymentId, 5, SubscriptionTierBands.Starter, Now));
    }

    [Fact]
    public void MarkSucceeded_WhenPending_TransitionsAndStoresThePaymentMethodIdAndPeriodEnd()
    {
        var subscription = BillingSubscription.Create(
            new BillingSubscriptionId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), "pmt_123", 5, SubscriptionTierBands.Starter, Now);

        subscription.MarkSucceeded("card_abc", Now);

        Assert.Equal(BillingSubscriptionStatus.Succeeded, subscription.Status);
        Assert.Equal("card_abc", subscription.PaymentMethodId);
        Assert.Equal(Now + BillingSubscription.PeriodLength, subscription.CurrentPeriodEnd);
    }

    [Fact]
    public void MarkSucceeded_WhenAlreadyTerminal_Throws()
    {
        var subscription = BillingSubscription.Create(
            new BillingSubscriptionId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), "pmt_123", 5, SubscriptionTierBands.Starter, Now);
        subscription.MarkSucceeded("card_abc", Now);

        Assert.Throws<InvalidOperationException>(() => subscription.MarkSucceeded("card_def", Now));
    }

    [Fact]
    public void MarkFailed_WhenPending_Transitions()
    {
        var subscription = BillingSubscription.Create(
            new BillingSubscriptionId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), "pmt_123", 5, SubscriptionTierBands.Starter, Now);

        subscription.MarkFailed();

        Assert.Equal(BillingSubscriptionStatus.Failed, subscription.Status);
        Assert.Null(subscription.PaymentMethodId);
    }

    [Fact]
    public void MarkFailed_WhenAlreadyTerminal_Throws()
    {
        var subscription = BillingSubscription.Create(
            new BillingSubscriptionId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), "pmt_123", 5, SubscriptionTierBands.Starter, Now);
        subscription.MarkFailed();

        Assert.Throws<InvalidOperationException>(() => subscription.MarkFailed());
    }

    private static BillingSubscription CreateSucceeded(int seats = 5, string tier = SubscriptionTierBands.Starter)
    {
        var subscription = BillingSubscription.Create(
            new BillingSubscriptionId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), "pmt_123", seats, tier, Now);
        subscription.MarkSucceeded("card_abc", Now);
        return subscription;
    }

    [Fact]
    public void RecordRenewalFailure_WhenSucceeded_TransitionsToPastDueAndStampsPastDueSince()
    {
        var subscription = CreateSucceeded();
        var failedAt = Now + BillingSubscription.PeriodLength;

        subscription.RecordRenewalFailure(failedAt);

        Assert.Equal(BillingSubscriptionStatus.PastDue, subscription.Status);
        Assert.Equal(failedAt, subscription.PastDueSince);
        Assert.Equal(failedAt, subscription.LastRenewalAttemptAt);
    }

    [Fact]
    public void RecordRenewalFailure_WhenNotSucceeded_Throws()
    {
        var subscription = BillingSubscription.Create(
            new BillingSubscriptionId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), "pmt_123", 5, SubscriptionTierBands.Starter, Now);

        Assert.Throws<InvalidOperationException>(() => subscription.RecordRenewalFailure(Now));
    }

    [Fact]
    public void RecordRenewalRetryFailure_WhenPastDue_MovesLastRenewalAttemptButNotPastDueSince()
    {
        var subscription = CreateSucceeded();
        var firstFailure = Now + BillingSubscription.PeriodLength;
        subscription.RecordRenewalFailure(firstFailure);

        var retryAt = firstFailure + TimeSpan.FromDays(1);
        subscription.RecordRenewalRetryFailure(retryAt);

        Assert.Equal(BillingSubscriptionStatus.PastDue, subscription.Status);
        Assert.Equal(firstFailure, subscription.PastDueSince);
        Assert.Equal(retryAt, subscription.LastRenewalAttemptAt);
    }

    [Fact]
    public void RecordRenewalSuccess_FromPastDue_ClearsPastDueAndAdvancesPeriodFromItself()
    {
        var subscription = CreateSucceeded();
        var periodEndBeforeFailure = subscription.CurrentPeriodEnd!.Value;
        var failedAt = periodEndBeforeFailure;
        subscription.RecordRenewalFailure(failedAt);

        // A late-paying retry, several days into the grace window - the next period must extend from
        // the original period end, not from "now", or the retry would shorten the customer's own
        // next period just for having paid late.
        var retrySucceededAt = failedAt + TimeSpan.FromDays(3);
        subscription.RecordRenewalSuccess(retrySucceededAt, paymentMethodId: null);

        Assert.Equal(BillingSubscriptionStatus.Succeeded, subscription.Status);
        Assert.Null(subscription.PastDueSince);
        Assert.Equal(periodEndBeforeFailure + BillingSubscription.PeriodLength, subscription.CurrentPeriodEnd);
    }

    [Fact]
    public void RecordRenewalSuccess_WithAPendingDowngrade_AppliesAndClearsIt()
    {
        var subscription = CreateSucceeded(seats: 20, tier: SubscriptionTierBands.Growth);
        subscription.ScheduleSeatDecrease(5, SubscriptionTierBands.Starter);

        var renewalAt = subscription.CurrentPeriodEnd!.Value;
        subscription.RecordRenewalSuccess(renewalAt, paymentMethodId: null);

        Assert.Equal(5, subscription.RequestedSeats);
        Assert.Equal(SubscriptionTierBands.Starter, subscription.Tier);
        Assert.Null(subscription.PendingSeatCount);
        Assert.Null(subscription.PendingTier);
    }

    [Fact]
    public void HasExhaustedRetryWindow_BeforeSevenDays_IsFalse_AtOrAfter_IsTrue()
    {
        var subscription = CreateSucceeded();
        var failedAt = subscription.CurrentPeriodEnd!.Value;
        subscription.RecordRenewalFailure(failedAt);

        Assert.False(subscription.HasExhaustedRetryWindow(failedAt + BillingSubscription.PastDueRetryWindow - TimeSpan.FromSeconds(1)));
        Assert.True(subscription.HasExhaustedRetryWindow(failedAt + BillingSubscription.PastDueRetryWindow));
    }

    [Fact]
    public void IsRetryDue_ImmediatelyAfterFailure_IsFalse_UntilARetryIntervalHasPassed()
    {
        var subscription = CreateSucceeded();
        var failedAt = subscription.CurrentPeriodEnd!.Value;
        subscription.RecordRenewalFailure(failedAt);

        Assert.False(subscription.IsRetryDue(failedAt + TimeSpan.FromHours(1)));
        Assert.True(subscription.IsRetryDue(failedAt + BillingSubscription.RetryInterval));
    }

    [Fact]
    public void MarkLapsed_FromPastDue_Transitions()
    {
        var subscription = CreateSucceeded();
        subscription.RecordRenewalFailure(subscription.CurrentPeriodEnd!.Value);

        subscription.MarkLapsed();

        Assert.Equal(BillingSubscriptionStatus.Lapsed, subscription.Status);
    }

    [Fact]
    public void MarkLapsed_FromPending_Throws()
    {
        var subscription = BillingSubscription.Create(
            new BillingSubscriptionId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), "pmt_123", 5, SubscriptionTierBands.Starter, Now);

        Assert.Throws<InvalidOperationException>(() => subscription.MarkLapsed());
    }

    [Fact]
    public void RequestCancellation_WhenSucceeded_SetsTheFlagWithoutTouchingStatusOrPeriodEnd()
    {
        var subscription = CreateSucceeded();
        var periodEnd = subscription.CurrentPeriodEnd;

        subscription.RequestCancellation(Now);

        Assert.True(subscription.CancelRequested);
        Assert.Equal(BillingSubscriptionStatus.Succeeded, subscription.Status);
        Assert.Equal(periodEnd, subscription.CurrentPeriodEnd);
    }

    [Fact]
    public void ApplySeatIncreaseImmediately_MovesRequestedSeatsAndTierRightAway()
    {
        var subscription = CreateSucceeded(seats: 5, tier: SubscriptionTierBands.Starter);

        subscription.ApplySeatIncreaseImmediately(15, SubscriptionTierBands.Growth);

        Assert.Equal(15, subscription.RequestedSeats);
        Assert.Equal(SubscriptionTierBands.Growth, subscription.Tier);
    }

    [Fact]
    public void ApplySeatIncreaseImmediately_WithALowerOrEqualSeatCount_Throws()
    {
        var subscription = CreateSucceeded(seats: 5);

        Assert.Throws<ArgumentOutOfRangeException>(() => subscription.ApplySeatIncreaseImmediately(5, SubscriptionTierBands.Starter));
        Assert.Throws<ArgumentOutOfRangeException>(() => subscription.ApplySeatIncreaseImmediately(3, SubscriptionTierBands.Starter));
    }

    [Fact]
    public void ScheduleSeatDecrease_RecordsAPendingChangeWithoutTouchingRequestedSeats()
    {
        var subscription = CreateSucceeded(seats: 20, tier: SubscriptionTierBands.Growth);

        subscription.ScheduleSeatDecrease(5, SubscriptionTierBands.Starter);

        Assert.Equal(20, subscription.RequestedSeats);
        Assert.Equal(SubscriptionTierBands.Growth, subscription.Tier);
        Assert.Equal(5, subscription.PendingSeatCount);
        Assert.Equal(SubscriptionTierBands.Starter, subscription.PendingTier);
    }

    [Fact]
    public void ScheduleSeatDecrease_WithAHigherOrEqualSeatCount_Throws()
    {
        var subscription = CreateSucceeded(seats: 5);

        Assert.Throws<ArgumentOutOfRangeException>(() => subscription.ScheduleSeatDecrease(5, SubscriptionTierBands.Starter));
        Assert.Throws<ArgumentOutOfRangeException>(() => subscription.ScheduleSeatDecrease(10, SubscriptionTierBands.Growth));
    }

    [Fact]
    public void IsDueForRenewal_BeforePeriodEnd_IsFalse_AtOrAfter_IsTrue()
    {
        var subscription = CreateSucceeded();
        var periodEnd = subscription.CurrentPeriodEnd!.Value;

        Assert.False(subscription.IsDueForRenewal(periodEnd - TimeSpan.FromSeconds(1)));
        Assert.True(subscription.IsDueForRenewal(periodEnd));
    }
}
