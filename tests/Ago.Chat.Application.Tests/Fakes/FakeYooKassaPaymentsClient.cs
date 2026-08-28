using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeYooKassaPaymentsClient : IYooKassaPaymentsClient
{
    public CreatePaymentRequest? LastRequest { get; private set; }

    public CreatePaymentResult Result { get; set; } = new CreatePaymentResult.Success("pmt_fake", "https://yookassa.example/confirm");

    public Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(Result);
    }
}
