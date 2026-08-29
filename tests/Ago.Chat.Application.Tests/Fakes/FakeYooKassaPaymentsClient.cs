using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeYooKassaPaymentsClient : IYooKassaPaymentsClient
{
    public CreatePaymentRequest? LastRequest { get; private set; }

    public CreatePaymentResult Result { get; set; } = new CreatePaymentResult.Success("pmt_fake", "https://yookassa.example/confirm");

    public ChargeStoredPaymentMethodRequest? LastChargeRequest { get; private set; }

    public ChargeStoredPaymentMethodResult ChargeResult { get; set; } = new ChargeStoredPaymentMethodResult.Success("pmt_fake_charge");

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
}
