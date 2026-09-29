using Ago.Chat.Application.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.ProcessYooKassaWebhook;

/// <summary>
/// `26-286`: the trust boundary of the whole webhook path lives here, not in the endpoint. ЮKassa does
/// not sign its console-configured HTTP notifications (there is no HMAC header and no shared webhook key
/// to compare against - `adr/0071`'s assumption, made without network access to confirm it, was wrong;
/// superseded by `adr/0190`).
/// Its own documented verification is instead <b>re-query the payment object by id and act on the API's
/// authoritative status</b>. So this handler:
/// <list type="number">
/// <item>calls <see cref="IYooKassaPaymentsClient.GetPaymentAsync"/> with the id the notification named -
/// the one and only thing it takes from the untrusted body, and only as a lookup key;</item>
/// <item>if ЮKassa has no such payment (<see cref="GetPaymentResult.NotFound"/>) - a forged or mistyped
/// notification - does nothing and reports <see cref="BillingWebhookApplyResult.Ignored"/> (the endpoint
/// still acks `200`: retrying will never make a nonexistent payment appear);</item>
/// <item>otherwise maps the <b>authoritative</b> status to this codebase's own canonical event string and
/// hands <see cref="IBillingWebhookApplier"/> that, plus the authoritative saved payment-method id -
/// never the notification's own claimed values.</item>
/// </list>
///
/// <para><b>Why the re-query belongs here, behind the payments-client port, and not in the applier.</b>
/// <see cref="IBillingWebhookApplier"/> is a persistence port - its one job is the single database
/// transaction (ledger, then subscription plus site) and it must stay a pure database concern with no
/// outbound HTTP in it. The re-query is an outbound call to a third party and belongs behind
/// <see cref="IYooKassaPaymentsClient"/>, orchestrated by this Application handler: fetch through port A,
/// then persist the authoritative outcome through port B. Folding the HTTP call into the Postgres applier
/// would make a database adapter depend on the payments client and mix a network I/O concern into a
/// transaction boundary - the alternative, and the wrong one under the dependency rule.</para>
///
/// <para>The tie to "a subscription/site we actually issued" is not a metadata round-trip: the applier
/// resolves the one `billing_subscriptions` (or `download_overage_charges`) row whose stored
/// `YooKassaPaymentId` equals this id - a value this deployment saved from ЮKassa's own authoritative
/// create-payment reply at checkout, never a value any caller supplies. A re-queried payment whose id
/// matches no row of ours resolves to <see cref="BillingWebhookApplyResult.SubscriptionNotFound"/> and
/// grants nothing.</para>
/// </summary>
public sealed class ProcessYooKassaWebhookHandler(
    IYooKassaPaymentsClient payments, IBillingWebhookApplier applier, IClock clock)
{
    private const string PaymentSucceededEvent = "payment.succeeded";

    private const string PaymentCanceledEvent = "payment.canceled";

    public async Task<BillingWebhookApplyResult> HandleAsync(ProcessYooKassaWebhook command, CancellationToken cancellationToken)
    {
        var payment = await payments.GetPaymentAsync(command.YooKassaPaymentId, cancellationToken);
        if (payment is not GetPaymentResult.Found found)
        {
            // ЮKassa has no record of this id - a forged or mistyped notification. Nothing to apply, and
            // no ledger row: an unknown id must not populate our idempotency ledger. Acked 200 upstream.
            return new BillingWebhookApplyResult.Ignored();
        }

        var eventType = MapAuthoritativeStatusToEvent(found);
        if (eventType is null)
        {
            // A real payment of ours, but in a non-terminal state this deployment does not act on
            // (`pending`, `waiting_for_capture`) - the authoritative status says so, regardless of what
            // the notification's `event` field claimed.
            return new BillingWebhookApplyResult.Ignored();
        }

        return await applier.ApplyAsync(
            new BillingWebhookApplyRequest(found.PaymentId, eventType, found.PaymentMethodId, clock.UtcNow),
            cancellationToken);
    }

    /// <summary>Maps ЮKassa's own authoritative payment status to the canonical event string
    /// <see cref="IBillingWebhookApplier"/> already keys its ledger and its terminal-state transitions on.
    /// A `succeeded` payment must also read `paid = true` - the two are documented to move together, and
    /// requiring both refuses to grant on a half-formed status this deployment has never observed rather
    /// than guessing.</summary>
    private static string? MapAuthoritativeStatusToEvent(GetPaymentResult.Found payment) => payment.Status switch
    {
        "succeeded" when payment.Paid => PaymentSucceededEvent,
        "canceled" => PaymentCanceledEvent,
        _ => null,
    };
}
