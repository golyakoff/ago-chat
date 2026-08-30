using Ago.Chat.Infrastructure.YandexGpt;
using Ago.Platform.Resilience;
using Polly;

namespace Ago.Chat.Module.ReplyDraft;

/// <summary>
/// `19-01`: the resilience wrapping the reply-draft feature needs around
/// `Ago.Chat.Infrastructure.YandexGpt.YandexGptReplyDraftClient` - built from the same
/// `Ago.Platform.Resilience` building blocks as `Ago.Chat.Module.Billing.BillingResiliencePipeline` and
/// `Ago.Chat.Module.Channels.ChannelResiliencePipelines`, not a fourth hand-rolled Polly setup.
///
/// <para><b>One pipeline, not keyed per anything</b> - the identical simplification
/// `BillingResiliencePipeline`'s own remarks make for the identical reason: this codebase talks to
/// exactly one LLM provider, called from exactly one use case, so there is nothing here for a
/// dictionary key to distinguish.</para>
///
/// <para>Registered as a singleton (`ChatModule`) - a scoped or transient lifetime would silently
/// rebuild a fresh, un-tripped breaker per DI scope, the same note every other resilience-pipeline
/// wrapper in this codebase carries.</para>
/// </summary>
public sealed class ReplyDraftResiliencePipeline
{
    public const string PipelineName = "ReplyDraft";

    private readonly Lazy<ResiliencePipeline> _pipeline;

    public ReplyDraftResiliencePipeline(ResiliencePipelineOptions options) => _pipeline = new Lazy<ResiliencePipeline>(() => Build(options));

    public ResiliencePipeline Pipeline => _pipeline.Value;

    private static ResiliencePipeline Build(ResiliencePipelineOptions options)
    {
        var builder = new ResiliencePolicyBuilder(PipelineName);

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

    /// <summary>Cancellation is never the provider's fault, and neither is a terminal, our-own-fault
    /// refusal (`ReplyDraftProviderRefusedException` - a bad key, a malformed request) - retrying or
    /// tripping the breaker on either would be reacting to something that is not evidence the provider
    /// itself is unhealthy, the same distinction `BillingResiliencePipeline.IsBreakerWorthy`'s own
    /// remarks draw for cancellation alone.</summary>
    private static bool IsBreakerWorthy(Exception ex) =>
        ex is not OperationCanceledException && ex is not ReplyDraftProviderRefusedException;

    /// <summary>Same exclusion as <see cref="IsBreakerWorthy"/>, for the identical reason: retrying a
    /// cancelled call wastes a drain's own time, and retrying a terminal refusal three times before
    /// giving up would waste real money against a real per-call budget for an outcome no retry could
    /// ever change (`ReplyDraftProviderRefusedException`'s own remarks).</summary>
    private static bool IsRetryWorthy(Exception ex) =>
        ex is not OperationCanceledException && ex is not ReplyDraftProviderRefusedException;
}
