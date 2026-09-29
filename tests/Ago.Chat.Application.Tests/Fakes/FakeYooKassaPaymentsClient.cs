using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeYooKassaPaymentsClient : IYooKassaPaymentsClient
{
    public CreatePaymentRequest? LastRequest { get; private set; }

    public CreatePaymentResult Result { get; set; } = new CreatePaymentResult.Success("pmt_fake", "https://yookassa.example/confirm");

    /// <summary>`26-291`: the token-payment call's own request/result pair, the identical shape
    /// <see cref="LastRequest"/>/<see cref="Result"/> already establish for the redirect flow. Defaults
    /// to a `pending` payment with a confirmation URL (the "SDK must run its own 3DS/SberPay confirmation
    /// intent" case) since that is the branch a caller not overriding this is most likely exercising;
    /// a test proving the "captured outright" case overrides <see cref="TokenResult"/> with a `null`
    /// confirmation URL.</summary>
    public CreatePaymentWithTokenRequest? LastTokenRequest { get; private set; }

    public CreatePaymentWithTokenResult TokenResult { get; set; } =
        new CreatePaymentWithTokenResult.Success("pmt_token_fake", "pending", "https://yookassa.example/confirm");

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

    public Task<CreatePaymentWithTokenResult> CreatePaymentWithTokenAsync(CreatePaymentWithTokenRequest request, CancellationToken cancellationToken)
    {
        LastTokenRequest = request;
        return Task.FromResult(TokenResult);
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
