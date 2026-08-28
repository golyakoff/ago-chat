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
    public void MarkSucceeded_WhenPending_TransitionsAndStoresThePaymentMethodId()
    {
        var subscription = BillingSubscription.Create(
            new BillingSubscriptionId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), "pmt_123", 5, SubscriptionTierBands.Starter, Now);

        subscription.MarkSucceeded("card_abc");

        Assert.Equal(BillingSubscriptionStatus.Succeeded, subscription.Status);
        Assert.Equal("card_abc", subscription.PaymentMethodId);
    }

    [Fact]
    public void MarkSucceeded_WhenAlreadyTerminal_Throws()
    {
        var subscription = BillingSubscription.Create(
            new BillingSubscriptionId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), "pmt_123", 5, SubscriptionTierBands.Starter, Now);
        subscription.MarkSucceeded("card_abc");

        Assert.Throws<InvalidOperationException>(() => subscription.MarkSucceeded("card_def"));
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
}
