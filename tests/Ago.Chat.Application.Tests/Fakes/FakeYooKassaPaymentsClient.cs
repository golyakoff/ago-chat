using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeYooKassaPaymentsClient : IYooKassaPaymentsClient
{
    public CreatePaymentRequest? LastRequest { get; private set; }

    public CreatePaymentResult Result { get; set; } = new CreatePaymentResult.Success("pmt_fake", "https://yookassa.example/confirm");

    public ChargeStoredPaymentMethodRequest? LastChargeRequest { get; private set; }

    public ChargeStoredPaymentMethodResult ChargeResult { get; set; } = new ChargeStoredPaymentMethodResult.Success("pmt_fake_charge");

    public string? LastGetPaymentId { get; private set; }

    /// <summary>`26-286`: the authoritative payment a re-query reads back. Defaults to a succeeded, paid
    /// payment so a test that does not care about the webhook path gets the common case for free; a
    /// webhook test overrides it (a NotFound for a forged id, a non-terminal status, a cancellation).</summary>
    public GetPaymentResult GetPaymentResult { get; set; } =
        new GetPaymentResult.Found("pmt_fake", "succeeded", Paid: true, PaymentMethodId: "card_fake");

    public Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(Result);
    }

    public Task<ChargeStoredPaymentMethodResult> ChargeStoredPaymentMethodAsync(
        ChargeStoredPaymentMethodRequest request, CancellationToken cancellationToken)
    {
        LastChargeRequest = request;
        return Task.FromResult(ChargeResult);
    }

    public Task<GetPaymentResult> GetPaymentAsync(string paymentId, CancellationToken cancellationToken)
    {
        LastGetPaymentId = paymentId;
        return Task.FromResult(GetPaymentResult);
    }
}
