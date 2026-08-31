using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Module.PhoneVerification;

/// <summary>
/// `14-15`: wraps a real gateway's own `IPhoneVerificationSender` implementation in
/// <see cref="PhoneVerificationResiliencePipeline"/> - the same decorator shape
/// `Ago.Chat.Module.Channels.ResilientInboundChannelAdapter`/`Ago.Chat.Module.Billing.ResilientYooKassaPaymentsClient`
/// already establish: composition in the composition root, not inheritance every gateway client would
/// have to remember to opt into.
///
/// <para><b>Lets an exhausted-retry fault propagate, rather than degrading it - `ResilientInboundChannelAdapter`'s
/// own shape, not `ResilientReplyDraftGenerator`'s.</b> This item's own backlog brief states the choice
/// plainly: the only caller of this class is `Ago.Chat.Worker`'s own `PhoneVerificationDeliveryConsumer`,
/// which already has its own retry/DLQ backstop (the identical reason `ChannelMessageDeliveryConsumer`'s
/// own remarks give for why `ResilientInboundChannelAdapter` does not catch either) - unlike
/// `ResilientReplyDraftGenerator`'s caller, a synchronous HTTP request with no later "run" for a failure
/// to wait for. Catching and degrading here would just turn a visible, dead-letterable failure into a
/// silently dropped verification code.</para>
///
/// <para>Not registered by `ChatModule` today - there is no real gateway client to wrap
/// (<see cref="UnconfiguredPhoneVerificationSender"/>'s own remarks on why that type is what is actually
/// registered). Exists and is unit-tested so the shape is real now, not invented the day an account
/// exists.</para>
/// </summary>
public sealed class ResilientPhoneVerificationSender(
    IPhoneVerificationSender inner, PhoneVerificationResiliencePipeline pipeline) : IPhoneVerificationSender
{
    // Polly's own non-generic ExecuteAsync(Func<CancellationToken, ValueTask>, CancellationToken) overload -
    // this call has nothing to hand back, so there is no need for the value-returning overload every other
    // Resilient* decorator in this codebase uses for its own non-void inner call.
    public Task SendCodeAsync(PhoneVerificationDelivery delivery, CancellationToken cancellationToken) =>
        pipeline.Pipeline.ExecuteAsync(
            async token => await inner.SendCodeAsync(delivery, token), cancellationToken).AsTask();
}
