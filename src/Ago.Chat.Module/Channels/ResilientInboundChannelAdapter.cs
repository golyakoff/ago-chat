using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Module.Channels;

/// <summary>
/// `14-01`: wraps any <see cref="IInboundChannelAdapter"/> in its channel's
/// <see cref="ChannelResiliencePipelines"/> pipeline, so `14-02`'s MAX adapter and `14-03`'s SMS
/// adapter each get timeout, retry, circuit breaker and bulkhead without writing a line of Polly.
///
/// <para><b>A decorator rather than a base class every adapter inherits.</b> Inheritance would make
/// resilience opt-in - an adapter author who forgot the base class would ship an unprotected call to a
/// third party, and nothing would notice until that provider hung. Composition puts the decision in
/// the composition root, where clean-architecture.md says wiring belongs, and keeps the adapter itself
/// a plain "call the provider, translate the answer" class that is trivial to unit-test with no
/// pipeline at all. It is the same relationship <c>RedisCache</c> and <c>S3FileStorage</c> have with
/// their own pipelines, expressed as a separate type here because the thing being protected is
/// somebody else's implementation rather than our own.</para>
///
/// <para><see cref="Kind"/> is forwarded verbatim: a decorator that answered anything else would break
/// <see cref="InboundChannelAdapterRegistry"/>'s lookup, since the registry keys on exactly this
/// property and cannot see through the wrapper.</para>
/// </summary>
public sealed class ResilientInboundChannelAdapter(
    IInboundChannelAdapter inner, ChannelResiliencePipelines pipelines) : IInboundChannelAdapter
{
    public ChannelKind Kind => inner.Kind;

    public async Task<ChannelSendOutcome> SendAsync(
        OutboundChannelMessage message, CancellationToken cancellationToken)
    {
        // The pipeline for the *inner* adapter's channel, not the message's - they are always the same
        // in a correctly-wired host, and preferring the adapter's own answer means a mis-addressed
        // message can never borrow another provider's breaker state.
        var pipeline = pipelines.For(inner.Kind);

        // ExecuteAsync's own token is passed through to the inner call: Polly's timeout strategy links
        // its internal token to this one, so a caller cancelling (a host draining) and a timeout firing
        // are distinguishable at the inner adapter - which is what lets both predicates in
        // ChannelResiliencePipelines exclude OperationCanceledException honestly.
        return await pipeline.ExecuteAsync(
            async token => await inner.SendAsync(message, token), cancellationToken);
    }
}
