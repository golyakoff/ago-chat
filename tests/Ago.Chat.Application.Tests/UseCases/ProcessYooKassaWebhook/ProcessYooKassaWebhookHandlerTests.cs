using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.ProcessYooKassaWebhook;

namespace Ago.Chat.Application.Tests.UseCases.ProcessYooKassaWebhook;

/// <summary>
/// `26-286`: the handler's trust boundary. ЮKassa does not sign its notifications, so the handler must
/// re-query the payment and act only on the authoritative reply. These prove: a forged/unknown id (the
/// re-query says NotFound) grants nothing and never touches the applier; a real but non-terminal status
/// grants nothing; a genuinely succeeded payment applies with the mapped event and the <b>re-queried</b>
/// payment-method id, not any value a caller could have supplied; and a canceled payment maps to the
/// cancel event.
/// </summary>
public sealed class ProcessYooKassaWebhookHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeYooKassaPaymentsClient _payments = new();

    private readonly FakeBillingWebhookApplier _applier = new();

    private ProcessYooKassaWebhookHandler BuildHandler() =>
        new(_payments, _applier, new FakeClock(Now));

    [Fact]
    public async Task WhenTheReQueryFindsNoSuchPayment_IgnoresAndNeverCallsTheApplier()
    {
        _payments.GetPaymentResult = new GetPaymentResult.NotFound();

        var result = await BuildHandler().HandleAsync(new Application.UseCases.ProcessYooKassaWebhook.ProcessYooKassaWebhook("pmt_forged"), CancellationToken.None);

        Assert.IsType<BillingWebhookApplyResult.Ignored>(result);
        Assert.Empty(_applier.Applied);
        Assert.Equal("pmt_forged", _payments.LastGetPaymentId);
    }

    [Fact]
    public async Task WhenTheReQueriedStatusIsNotTerminal_IgnoresAndNeverCallsTheApplier()
    {
        _payments.GetPaymentResult = new GetPaymentResult.Found("pmt_1", "pending", Paid: false, PaymentMethodId: null);

        var result = await BuildHandler().HandleAsync(new Application.UseCases.ProcessYooKassaWebhook.ProcessYooKassaWebhook("pmt_1"), CancellationToken.None);

        Assert.IsType<BillingWebhookApplyResult.Ignored>(result);
        Assert.Empty(_applier.Applied);
    }

    [Fact]
    public async Task WhenTheReQueriedStatusIsSucceededButNotPaid_IgnoresAndNeverCallsTheApplier()
    {
        // A half-formed status this deployment has never observed - succeeded and paid move together, and
        // requiring both refuses to grant rather than guessing.
        _payments.GetPaymentResult = new GetPaymentResult.Found("pmt_1", "succeeded", Paid: false, PaymentMethodId: "card_x");

        var result = await BuildHandler().HandleAsync(new Application.UseCases.ProcessYooKassaWebhook.ProcessYooKassaWebhook("pmt_1"), CancellationToken.None);

        Assert.IsType<BillingWebhookApplyResult.Ignored>(result);
        Assert.Empty(_applier.Applied);
    }

    [Fact]
    public async Task WhenTheReQuerySaysSucceeded_AppliesWithTheAuthoritativeEventAndPaymentMethod()
    {
        _payments.GetPaymentResult = new GetPaymentResult.Found("pmt_real", "succeeded", Paid: true, PaymentMethodId: "card_authoritative");

        await BuildHandler().HandleAsync(new Application.UseCases.ProcessYooKassaWebhook.ProcessYooKassaWebhook("pmt_real"), CancellationToken.None);

        var applied = Assert.Single(_applier.Applied);
        Assert.Equal("pmt_real", applied.YooKassaPaymentId);
        Assert.Equal("payment.succeeded", applied.EventType);
        Assert.Equal("card_authoritative", applied.PaymentMethodId);
        Assert.Equal(Now, applied.Now);
    }

    [Fact]
    public async Task WhenTheReQuerySaysCanceled_AppliesTheCancelEvent()
    {
        _payments.GetPaymentResult = new GetPaymentResult.Found("pmt_c", "canceled", Paid: false, PaymentMethodId: null);
        _applier.Result = new BillingWebhookApplyResult.Canceled();

        var result = await BuildHandler().HandleAsync(new Application.UseCases.ProcessYooKassaWebhook.ProcessYooKassaWebhook("pmt_c"), CancellationToken.None);

        Assert.IsType<BillingWebhookApplyResult.Canceled>(result);
        var applied = Assert.Single(_applier.Applied);
        Assert.Equal("payment.canceled", applied.EventType);
    }
}
