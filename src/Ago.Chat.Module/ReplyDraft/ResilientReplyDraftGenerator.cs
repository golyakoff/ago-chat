using Ago.Chat.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Ago.Chat.Module.ReplyDraft;

/// <summary>
/// `19-01`: wraps `Ago.Chat.Infrastructure.YandexGpt.YandexGptReplyDraftClient` in
/// <see cref="ReplyDraftResiliencePipeline"/> - the same decorator shape
/// `Ago.Chat.Module.Channels.ResilientInboundChannelAdapter`/`Ago.Chat.Module.Billing.ResilientYooKassaPaymentsClient`
/// already establish: composition in the composition root, not inheritance every implementation must
/// remember to opt into, and an Application handler (`GenerateReplyDraftHandler`) that stays unaware
/// resilience exists at all.
///
/// <para><b>Why this decorator catches, where those two do not.</b> `ResilientInboundChannelAdapter`
/// and `ResilientYooKassaPaymentsClient` both let a fault that survives the pipeline's own retries
/// propagate as an exception, because their callers have somewhere better for it to go: a channel send
/// runs inside `Ago.Chat.Module.Pipeline`'s own batch machinery with its own retry/DLQ story, and the
/// recurring-charge job is retried by `Ago.Chat.Worker`'s own job scheduler on its next run. A reply
/// draft has no such backstop - `GenerateReplyDraftHandler` runs inside one synchronous HTTP request an
/// operator is watching, and there is no later "run" for a failure to wait for. `19-01`'s own Done-when
/// asks for exactly this: "the resilience pipeline's own unreachable-provider path degrades to
/// 'suggestion unavailable', never a stuck or silently-failing UI control" - which makes this decorator,
/// not the handler above it, the correct place to end the resilience story, the identical
/// "catch and degrade rather than propagate" choice `Ago.Platform.Caching.Redis.RedisRateLimiter`'s own
/// remarks make for a Redis outage (`adr/0009`'s "fail open, never surface an error").</para>
/// </summary>
public sealed class ResilientReplyDraftGenerator(
    IReplyDraftGenerator inner, ReplyDraftResiliencePipeline pipeline, ILogger<ResilientReplyDraftGenerator> logger)
    : IReplyDraftGenerator
{
    public async Task<ReplyDraftGenerationResult> GenerateDraftAsync(
        ReplyDraftGenerationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await pipeline.Pipeline.ExecuteAsync(
                async token => await inner.GenerateDraftAsync(request, token), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The caller's own cancellation (the operator navigated away, the request was aborted) -
            // never logged as a provider failure, and never converted to Unavailable: the caller is
            // gone and will not read either outcome, so this simply propagates, the same
            // "cancellation is not this pipeline's business" rule its own IsBreakerWorthy/IsRetryWorthy
            // predicates already state.
            throw;
        }
        catch (Exception ex)
        {
            // Every other outcome the pipeline could not turn into a success - a broken circuit, a
            // timeout, an exhausted retry budget, or a terminal ReplyDraftProviderRefusedException -
            // degrades to the identical answer. Logged at Warning, not Error: an unavailable AI
            // suggestion is a degraded feature, not an incident (resilience.md's own "the assertion is
            // always about the rest of the system staying healthy while the dependency is broken").
            logger.LogWarning(ex, "Reply-draft provider unavailable; degrading to 'suggestion unavailable'.");
            return new ReplyDraftGenerationResult.Unavailable("The reply-draft suggestion is temporarily unavailable.");
        }
    }
}
