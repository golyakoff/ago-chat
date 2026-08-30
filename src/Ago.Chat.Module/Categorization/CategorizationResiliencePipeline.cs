using Ago.Chat.Infrastructure.YandexGpt;
using Ago.Platform.Resilience;
using Polly;

namespace Ago.Chat.Module.Categorization;

/// <summary>
/// `19-02`: the resilience wrapping `Ago.Chat.Infrastructure.YandexGpt.YandexGptConversationCategorizerClient`
/// needs - built from the same `Ago.Platform.Resilience` building blocks
/// `Ago.Chat.Module.ReplyDraft.ReplyDraftResiliencePipeline`/`Ago.Chat.Module.Billing.BillingResiliencePipeline`
/// already use, not a fifth hand-rolled Polly setup.
///
/// <para>One pipeline, not keyed per anything - the identical simplification those two pipelines' own
/// remarks make for the identical reason: one LLM provider, called from exactly one use case.</para>
///
/// <para>Registered as a singleton (`ChatModule`) - the same "a scoped/transient lifetime would
/// silently rebuild a fresh, un-tripped breaker" note every resilience-pipeline wrapper in this
/// codebase carries.</para>
/// </summary>
public sealed class CategorizationResiliencePipeline
{
    public const string PipelineName = "Categorization";

    private readonly Lazy<ResiliencePipeline> _pipeline;

    public CategorizationResiliencePipeline(ResiliencePipelineOptions options) =>
        _pipeline = new Lazy<ResiliencePipeline>(() => Build(options));

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

    /// <summary>Same exclusion <see cref="Ago.Chat.Module.ReplyDraft.ReplyDraftResiliencePipeline.IsBreakerWorthy"/>
    /// makes, for the identical reason: cancellation is never the provider's fault (the job's own
    /// shutdown, not a fault), and neither is a terminal, our-own-fault refusal (a bad key, a malformed
    /// request) - retrying or tripping the breaker on either would react to something that is not
    /// evidence the provider itself is unhealthy.</summary>
    private static bool IsBreakerWorthy(Exception ex) =>
        ex is not OperationCanceledException && ex is not ConversationCategorizationProviderRefusedException;

    /// <summary>Same exclusion as <see cref="IsBreakerWorthy"/>: retrying a terminal refusal up to
    /// <see cref="ResilienceRetryOptions.MaxRetryAttempts"/> times before giving up would waste real
    /// money against a real per-call budget for an outcome no retry could ever change - and this job
    /// iterates a whole batch per tick, so a bad key would otherwise be paid for once per candidate
    /// conversation rather than once.</summary>
    private static bool IsRetryWorthy(Exception ex) =>
        ex is not OperationCanceledException && ex is not ConversationCategorizationProviderRefusedException;
}
