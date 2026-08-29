using Ago.Platform.Resilience;
using Polly;

namespace Ago.Chat.Module.Billing;

/// <summary>
/// `13-03`: the resilience wrapping this item's own recurring-charge job needs around
/// <see cref="Ago.Chat.Application.Abstractions.IYooKassaPaymentsClient.ChargeStoredPaymentMethodAsync"/> -
/// built from the same <c>Ago.Platform.Resilience</c> building blocks as
/// <c>Ago.Chat.Module.Channels.ChannelResiliencePipelines</c>, not a fourth hand-rolled Polly setup.
/// `resilience.md`'s boundary table already names "a recurring-charge job calling ЮKassa" as exactly
/// this shape (`13-03`'s own backlog note).
///
/// <para><b>One pipeline, not keyed per anything.</b> `ChannelResiliencePipelines` keys per
/// <c>ChannelKind</c> because several independent providers share that class; this codebase talks to
/// exactly one payment provider, called from exactly one process-wide job, so there is nothing here for
/// a dictionary key to distinguish - the identical simplification `WebhookResiliencePipelines` would
/// make if it too only ever had one endpoint.</para>
///
/// <para>Registered as a singleton (`ChatModule`) - a scoped or transient lifetime would silently
/// rebuild a fresh, un-tripped breaker per DI scope, the same note every other resilience-pipeline
/// wrapper in this codebase carries.</para>
/// </summary>
public sealed class BillingResiliencePipeline
{
    public const string PipelineName = "Billing";

    private readonly Lazy<ResiliencePipeline> _pipeline;

    public BillingResiliencePipeline(ResiliencePipelineOptions options) => _pipeline = new Lazy<ResiliencePipeline>(() => Build(options));

    public ResiliencePipeline Pipeline => _pipeline.Value;

    private static ResiliencePipeline Build(ResiliencePipelineOptions options)
    {
        var builder = new ResiliencePolicyBuilder(PipelineName);

        // Every group is optional (ResiliencePipelineOptions' own remarks): a deployment that
        // configures only a timeout gets only a timeout - the same "do not invent thresholds here"
        // discipline ChannelResiliencePipelines' own remarks state.
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
    /// pipeline in this codebase applies, so a host shutting down does not leave a breaker open against
    /// a perfectly healthy ЮKassa on the next start.</summary>
    private static bool IsBreakerWorthy(Exception ex) => ex is not OperationCanceledException;

    /// <summary>Same exclusion, for the identical second reason
    /// <c>ChannelResiliencePipelines.IsRetryWorthy</c>'s own remarks give: retrying a cancelled call is
    /// pointless work during a drain. A terminal refusal (<c>ChargeStoredPaymentMethodResult.Refused</c>)
    /// never reaches here at all - that outcome is a return value, not a thrown exception, so everything
    /// this predicate ever sees is already a transient fault, and retrying it is safe precisely because
    /// the recurring-charge job's own deterministic <c>Idempotence-Key</c> makes a retried charge return
    /// the original payment's result rather than creating a second one
    /// (<c>ChargeStoredPaymentMethodRequest</c>'s own remarks).</summary>
    private static bool IsRetryWorthy(Exception ex) => ex is not OperationCanceledException;
}
