using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Module.Push;

/// <summary>
/// `26-04`: wraps <see cref="IPushSender"/> in <see cref="PushResiliencePipeline"/> - the same decorator
/// shape <c>Ago.Chat.Module.Channels.ResilientInboundChannelAdapter</c> and
/// <c>Ago.Chat.Module.Billing.ResilientYooKassaPaymentsClient</c> already establish: composition in the
/// composition root, not inheritance every implementation must remember to opt into, so
/// <c>Ago.Chat.Infrastructure.RuStore.RuStorePushSender</c> stays a plain "call RuStore, translate the
/// answer" class that is trivial to unit-test with no pipeline at all
/// (`Ago.Chat.Infrastructure.RuStore`'s own tests prove exactly that), and a future `26-05` handler
/// depending on <see cref="IPushSender"/> stays unaware resilience exists.
/// </summary>
public sealed class ResilientPushSender(IPushSender inner, PushResiliencePipeline pipeline) : IPushSender
{
    public Task<PushSendOutcome> SendAsync(PushMessage message, CancellationToken cancellationToken) =>
        pipeline.Pipeline.ExecuteAsync(async token => await inner.SendAsync(message, token), cancellationToken).AsTask();
}
