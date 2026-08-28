using Ago.Chat.Application.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.ProcessYooKassaWebhook;

/// <summary>
/// `13-02`: a thin pass-through to <see cref="IBillingWebhookApplier"/>, the port that actually owns
/// the one-transaction "ledger, then (on success) subscription plus site" shape this item's backlog
/// describes. Kept as its own handler rather than folding the applier call straight into the endpoint -
/// the same "Application orchestrates a use case, Infrastructure supplies persistence" split every
/// other handler in this codebase draws, even though this one's own orchestration is a single call.
///
/// <para>Returns <see cref="BillingWebhookApplyResult"/> directly rather than wrapping it in
/// <see cref="Result{T}"/> - every one of that type's cases (<c>Duplicate</c>,
/// <c>SubscriptionNotFound</c>, <c>Applied</c>, <c>Canceled</c>, <c>Ignored</c>) is a legitimate
/// outcome the endpoint acks `200` for (backlog: "still acked 200"), not a `Result`-shaped failure a
/// caller must branch on to decide the HTTP status - there is no failure case here once the signature
/// has already verified, which happens earlier, in the endpoint.</para>
/// </summary>
public sealed class ProcessYooKassaWebhookHandler(IBillingWebhookApplier applier, IClock clock)
{
    public Task<BillingWebhookApplyResult> HandleAsync(ProcessYooKassaWebhook command, CancellationToken cancellationToken) =>
        applier.ApplyAsync(
            new BillingWebhookApplyRequest(command.YooKassaPaymentId, command.EventType, command.PaymentMethodId, clock.UtcNow),
            cancellationToken);
}
