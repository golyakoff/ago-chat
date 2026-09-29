using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`26-286`: records the request `ProcessYooKassaWebhookHandler` handed the applier, so a unit
/// test can assert the handler drove it from the <b>authoritative</b> re-query (the mapped event string,
/// the re-queried payment-method id) rather than from anything a notification claimed - and can assert
/// the applier was not called at all for a forged id or a non-terminal status.</summary>
public sealed class FakeBillingWebhookApplier : IBillingWebhookApplier
{
    public List<BillingWebhookApplyRequest> Applied { get; } = [];

    public BillingWebhookApplyResult Result { get; set; } =
        new BillingWebhookApplyResult.Applied(new SiteId(Guid.NewGuid()), SubscriptionTierBands.Starter, 5);

    public Task<BillingWebhookApplyResult> ApplyAsync(BillingWebhookApplyRequest request, CancellationToken cancellationToken)
    {
        Applied.Add(request);
        return Task.FromResult(Result);
    }
}
