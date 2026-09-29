using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Module.Billing;

/// <summary>
/// `13-03`: wraps <see cref="IYooKassaPaymentsClient.ChargeStoredPaymentMethodAsync"/> in
/// <see cref="BillingResiliencePipeline"/> - the same decorator shape
/// <c>Ago.Chat.Module.Channels.ResilientInboundChannelAdapter</c> already establishes: composition in
/// the composition root, not inheritance every implementation must remember to opt into, and an
/// Application handler (<c>ProcessSubscriptionRenewalHandler</c>) that stays unaware resilience exists
/// at all.
///
/// <para><b><see cref="CreatePaymentAsync"/> is passed through unwrapped, deliberately.</b>
/// `13-02`'s own <c>CreateCheckoutSessionHandler</c> already reasoned through why that call gets no
/// resilience wrapping (a low-frequency, human-driven write inside one HTTP request - a transient
/// failure surfaces as an ordinary `5xx` the operator's own client can retry by clicking again); this
/// decorator only ever wraps the second method, so it changes nothing about that decision. A single
/// <see cref="IYooKassaPaymentsClient"/> registration serving both call sites, rather than two separate
/// interfaces, is what makes that split possible without <c>CreateCheckoutSessionHandler</c> having to
/// know a second implementation exists.</para>
/// </summary>
public sealed class ResilientYooKassaPaymentsClient(IYooKassaPaymentsClient inner, BillingResiliencePipeline pipeline) : IYooKassaPaymentsClient
{
    public Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken) =>
        inner.CreatePaymentAsync(request, cancellationToken);

    // `26-286`: passed through unwrapped, the same reasoning CreatePaymentAsync just above states - the
    // re-query runs inside one inbound webhook request this host must ack fast; a transient failure
    // surfaces as an ordinary 5xx and ЮKassa's own webhook retry re-drives the whole notification, so
    // there is no background loop here for BillingResiliencePipeline to keep alive.
    public Task<GetPaymentResult> GetPaymentAsync(string paymentId, CancellationToken cancellationToken) =>
        inner.GetPaymentAsync(paymentId, cancellationToken);

    public Task<ChargeStoredPaymentMethodResult> ChargeStoredPaymentMethodAsync(
        ChargeStoredPaymentMethodRequest request, CancellationToken cancellationToken) =>
        pipeline.Pipeline.ExecuteAsync(async token => await inner.ChargeStoredPaymentMethodAsync(request, token), cancellationToken).AsTask();
}
