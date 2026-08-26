using System.Collections.Concurrent;
using Ago.Chat.Domain;
using Ago.Platform.Resilience;
using Polly;

namespace Ago.Chat.Module.Channels;

/// <summary>
/// `14-01`: the stateful half of the resilience wrapping around an outbound channel-provider call -
/// the direct analogue of `6-05`'s <c>WebhookResiliencePipelines</c>, and built from the same
/// <c>Ago.Platform.Resilience</c> building blocks rather than a fourth hand-rolled Polly setup.
/// `resilience.md`'s boundary table now names "outbound channel provider APIs" as a row this covers.
///
/// <para><b>Keyed per <see cref="ChannelKind"/>, and that is the whole point.</b> A circuit breaker's
/// open/half-open state and a bulkhead's in-flight count mean nothing unless the <em>same</em>
/// <see cref="ResiliencePipeline"/> instance is reused across calls for the same key. The key here is
/// the channel, because the failure the breaker exists to contain is "this provider is down": an SMS
/// aggregator having an outage must not stop MAX replies from being delivered, and a global pipeline
/// would do exactly that. Every channel shares the same configured thresholds (one
/// <c>Resilience:Channels:*</c> section); only the instance, and therefore the state, differs.</para>
///
/// <para><b>Why not per tenant, the way `6-05` bulkheads per site.</b> The webhook dispatcher calls an
/// endpoint each tenant chose, so one tenant's dead CRM is one tenant's problem and the blast radius
/// is naturally per tenant. A channel provider is chosen by <em>us</em> and shared by every tenant on
/// it, so per-tenant keys would produce N breakers all observing the same single outage and each
/// needing its own <c>MinimumThroughput</c> before reacting - slower to open and no better isolated.
/// The bulkhead is per channel for the matching reason: it bounds how much of this process's
/// concurrency one provider may hold.</para>
///
/// <para><b>Registered as a singleton</b> (<c>ChatModule</c>). A scoped or transient lifetime would
/// silently rebuild a fresh, un-tripped breaker per DI scope, which is the failure mode this type
/// exists to prevent - the identical note <c>WebhookResiliencePipelines</c> carries.</para>
///
/// <para>Composition order is CircuitBreaker outermost, then Retry, then Timeout - and the outermost
/// position is not a style choice. `6-05` found the hard way (against a real hanging fake provider,
/// not by reading Polly's source) that a breaker sitting <em>inside</em> a timeout never sees
/// <c>TimeoutRejectedException</c> at all, because Polly converts its internal cancellation into that
/// type on the way back out, one layer further out than the breaker. See
/// <c>WebhookResiliencePipelines.GetEndpointPipeline</c>'s own remarks for the full account; this
/// class inherits the conclusion rather than rediscovering it.</para>
/// </summary>
public sealed class ChannelResiliencePipelines(ResiliencePipelineOptions options)
{
    public const string PipelineName = "Channels";

    private readonly ConcurrentDictionary<ChannelKind, ResiliencePipeline> _byChannel = new();

    public ResiliencePipeline For(ChannelKind kind) =>
        _byChannel.GetOrAdd(kind, _ => Build(options));

    private static ResiliencePipeline Build(ResiliencePipelineOptions options)
    {
        var builder = new ResiliencePolicyBuilder(PipelineName);

        // Every group is optional (ResiliencePipelineOptions' own remarks): a deployment that
        // configures only a timeout gets only a timeout. Building a strategy from a null group would
        // mean inventing thresholds here, which CLAUDE.md rules out as much as inventing a benchmark.
        if (options.Bulkhead is { } bulkhead)
        {
            builder.WithBulkhead(bulkhead);
        }

        if (options.CircuitBreaker is { } breaker)
        {
            builder.WithCircuitBreaker(breaker, IsBreakerWorthy);
        }

        if (options.Retry is { } retry)
        {
            builder.WithRetry(retry, IsRetryWorthy);
        }

        if (options.Timeout is { } timeout)
        {
            builder.WithTimeout(timeout);
        }

        return builder.Build();
    }

    /// <summary>Cancellation is never the provider's fault - the same convention <c>RedisCache</c>,
    /// <c>S3FileStorage</c> and <c>WebhookResiliencePipelines</c> all apply, so a host shutting down
    /// does not leave a breaker open against a perfectly healthy provider on the next start.</summary>
    private static bool IsBreakerWorthy(Exception ex) => ex is not OperationCanceledException;

    /// <summary>
    /// Same exclusion, for a second reason worth stating separately: retrying a cancelled call is
    /// pointless work during a drain.
    ///
    /// <para>Note what is <em>not</em> excluded, and why it is safe: a terminal provider refusal never
    /// reaches here at all, because <see cref="Ago.Chat.Application.Abstractions.IInboundChannelAdapter.SendAsync"/>'s
    /// contract puts that on the return value rather than in an exception. So everything this
    /// predicate ever sees is already a transient fault, and retrying it is safe precisely because
    /// <see cref="Ago.Chat.Application.Abstractions.OutboundChannelMessage.MessageId"/> gives the
    /// provider a stable idempotency key - `resilience.md`'s own rule that retry without an
    /// idempotency key is a duplication mechanism.</para>
    /// </summary>
    private static bool IsRetryWorthy(Exception ex) => ex is not OperationCanceledException;
}
