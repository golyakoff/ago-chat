using Ago.Chat.Application.Abstractions;
using Ago.Platform.Resilience;
using Polly;

namespace Ago.Chat.Module.PhoneVerification;

/// <summary>
/// `14-15`: the resilience wrapping a real phone-verification gateway call would need, built from the
/// same `Ago.Platform.Resilience` building blocks as `ReplyDraftResiliencePipeline`/`BillingResiliencePipeline` -
/// not a fourth hand-rolled Polly setup, and the identical "one pipeline, not keyed per anything" shape
/// those two already establish, for the identical reason: this item talks to exactly one kind of gateway,
/// called from exactly one consumer, so there is nothing here for a dictionary key to distinguish.
///
/// <para>Registered as a singleton (`ChatModule`) - a scoped or transient lifetime would silently rebuild
/// a fresh, un-tripped breaker per DI scope, the same note every other resilience-pipeline wrapper in this
/// codebase carries. Exists and is unit-tested even though nothing in this deployment's own registration
/// currently routes a real call through it (<see cref="UnconfiguredPhoneVerificationSender"/>'s own
/// remarks) - this item's own backlog file: "build `IPhoneVerificationSender` so either SMS or voice, and
/// either vendor, is pluggable"; this class is half of what makes that true today, ready for
/// <see cref="ResilientPhoneVerificationSender"/> to wrap a real gateway client the day one exists.</para>
/// </summary>
public sealed class PhoneVerificationResiliencePipeline
{
    public const string PipelineName = "PhoneVerification";

    private readonly Lazy<ResiliencePipeline> _pipeline;

    public PhoneVerificationResiliencePipeline(ResiliencePipelineOptions options) =>
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

    /// <summary>Cancellation is never the gateway's fault, and neither is a terminal, retry-proof refusal
    /// (<see cref="PhoneVerificationSenderRefusedException"/> - a number the gateway will never accept, no
    /// account configured) - the identical distinction `ReplyDraftResiliencePipeline.IsBreakerWorthy`'s
    /// own remarks draw, for the identical reason: retrying or tripping the breaker on either would react
    /// to something that is not evidence the gateway itself is unhealthy.</summary>
    private static bool IsBreakerWorthy(Exception ex) =>
        ex is not OperationCanceledException && ex is not PhoneVerificationSenderRefusedException;

    /// <summary>Same exclusion as <see cref="IsBreakerWorthy"/>, for the identical cost-avoidance reason
    /// `ReplyDraftResiliencePipeline.IsRetryWorthy`'s own remarks state: this call is billed per attempt,
    /// so retrying a refusal three times before giving up spends real money on an outcome no retry could
    /// change.</summary>
    private static bool IsRetryWorthy(Exception ex) =>
        ex is not OperationCanceledException && ex is not PhoneVerificationSenderRefusedException;
}
