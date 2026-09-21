using Ago.Platform.Resilience;
using Polly;

namespace Ago.Chat.Module.Push;

/// <summary>
/// `26-04`: the resilience wrapping `Ago.Chat.Infrastructure.RuStore.RuStorePushSender` needs - built
/// from the same <c>Ago.Platform.Resilience</c> building blocks as
/// <c>Ago.Chat.Module.Billing.BillingResiliencePipeline</c>, not a fourth (well, eighth) hand-rolled
/// Polly setup. `resilience.md`'s boundary table names "outbound channel provider APIs" as this shape;
/// RuStore Push is the seventh integration in `Ago.Chat.Worker` to carry it
/// (`push-notifications.md`'s own "Which hosts change": "a seventh outbound integration in the Worker is
/// the boring answer and the right one").
///
/// <para><b>One pipeline, not keyed per anything</b> - <see cref="BillingResiliencePipeline"/>'s own
/// simplification, for the identical reason: this codebase talks to exactly one push provider
/// (`adr/0179` §5 refuses a provider registry until a real second one exists), called from exactly one
/// Worker-side port, so there is nothing here for a dictionary key to distinguish the way
/// <c>ChannelResiliencePipelines</c> keys per <see cref="Domain.ChannelKind"/> for six independent
/// providers.</para>
///
/// <para>Registered as a singleton in <c>Ago.Chat.Worker/Program.cs</c> (never
/// <c>ChatModule.ConfigureServices</c>, which every serving host calls -
/// <c>Ago.Chat.Infrastructure.RuStore.RuStoreOptions</c>'s own remarks explain why the whole of this
/// feature's wiring stays out of that shared method). A scoped or transient lifetime would silently
/// rebuild a fresh, un-tripped
/// breaker per DI scope, the same note every other resilience-pipeline wrapper in this codebase
/// carries.</para>
/// </summary>
public sealed class PushResiliencePipeline
{
    public const string PipelineName = "Push";

    private readonly Lazy<ResiliencePipeline> _pipeline;

    public PushResiliencePipeline(ResiliencePipelineOptions options) => _pipeline = new(() => Build(options));

    public ResiliencePipeline Pipeline => _pipeline.Value;

    private static ResiliencePipeline Build(ResiliencePipelineOptions options)
    {
        var builder = new ResiliencePolicyBuilder(PipelineName);

        // Every group is optional (ResiliencePipelineOptions' own remarks): a deployment that
        // configures only a timeout gets only a timeout - the same "do not invent thresholds here"
        // discipline every other resilience pipeline in this codebase carries.
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

    /// <summary>Cancellation is never the provider's fault - the same convention every other resilience
    /// pipeline in this codebase applies, so a host shutting down does not leave a breaker open against a
    /// perfectly healthy RuStore on the next start.</summary>
    private static bool IsBreakerWorthy(Exception ex) => ex is not OperationCanceledException;

    /// <summary>
    /// Same exclusion, for the identical second reason every other resilience pipeline's own remarks
    /// give: retrying a cancelled call is pointless work during a drain.
    ///
    /// <para>Note what is <em>not</em> excluded, and why it is safe to retry blindly here: every one of
    /// RuStore's own documented outcomes - terminal (device-fault) and transient (credential/overload)
    /// alike - is a <em>return value</em> from
    /// <c>Ago.Chat.Infrastructure.RuStore.RuStorePushSender.SendAsync</c>, never a thrown exception
    /// (<see cref="Application.Abstractions.IPushSender.SendAsync"/>'s own remarks). So everything this
    /// predicate ever sees is already the "the network or RuStore itself failed" case, and retrying a
    /// send that never actually reached RuStore's application layer cannot duplicate anything RuStore
    /// would have accepted.</para>
    ///
    /// <para><b>The one honest caveat this codebase's other channel adapters do not share.</b> Unlike
    /// VK's <c>random_id</c> or a webhook's own stable <c>MessageId</c>, RuStore's send API documents no
    /// idempotency key of its own - so a retry that races a send RuStore silently accepted anyway (the
    /// response lost to a dropped connection, say) can produce a duplicate push. `push-notifications.md`
    /// already names and accepts the comparable "a slow phone can receive more than one push for one
    /// conversation" cost for a different reason (no collapse key on the wire); this is the same shape
    /// of cost from a different cause, and it is bounded the identical way: a second buzz for the same
    /// conversation, not a corrupted counter, collapsed on arrival by the client's own notification tag.</para>
    /// </summary>
    private static bool IsRetryWorthy(Exception ex) => ex is not OperationCanceledException;
}
