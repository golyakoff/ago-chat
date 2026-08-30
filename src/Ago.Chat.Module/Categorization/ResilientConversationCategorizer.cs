using Ago.Chat.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Ago.Chat.Module.Categorization;

/// <summary>
/// `19-02`: wraps `Ago.Chat.Infrastructure.YandexGpt.YandexGptConversationCategorizerClient` in
/// <see cref="CategorizationResiliencePipeline"/> - the same decorator shape
/// `Ago.Chat.Module.ReplyDraft.ResilientReplyDraftGenerator` already establishes: composition in the
/// composition root, and `CategorizeConversationHandler` stays unaware resilience exists at all.
///
/// <para><b>Catches and degrades here too, for a different reason than `19-01`'s.</b>
/// <see cref="ResilientReplyDraftGenerator"/>'s own remarks explain why it degrades rather than
/// propagates: an operator is watching one synchronous HTTP request with no later "run" for a failure to
/// wait for. This class has no such operator, but it has an even plainer reason - `19-02`'s own Scope
/// ("a periodic batch job") means a fault that survives the pipeline must not stop
/// `Ago.Chat.Worker.ConversationCategorizationJob`'s own loop over the rest of its batch; degrading one
/// candidate to <see cref="CategorizationResult.Unavailable"/> lets the job move on to the next
/// candidate in the same tick, which a thrown exception would not.</para>
/// </summary>
public sealed class ResilientConversationCategorizer(
    IConversationCategorizer inner, CategorizationResiliencePipeline pipeline, ILogger<ResilientConversationCategorizer> logger)
    : IConversationCategorizer
{
    public async Task<CategorizationResult> CategorizeAsync(
        CategorizationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await pipeline.Pipeline.ExecuteAsync(
                async token => await inner.CategorizeAsync(request, token), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The job's own shutdown - never logged as a provider failure, propagated exactly as
            // ResilientReplyDraftGenerator's own remarks describe for the identical case.
            throw;
        }
        catch (Exception ex)
        {
            // Every other outcome the pipeline could not turn into a success - a broken circuit, a
            // timeout, an exhausted retry budget, or a terminal
            // ConversationCategorizationProviderRefusedException - degrades to the identical answer.
            // Logged at Warning, not Error: a skipped categorization cycle is a degraded feature, not
            // an incident (resilience.md).
            logger.LogWarning(ex, "Conversation categorizer unavailable; degrading to 'no categorization this cycle'.");
            return new CategorizationResult.Unavailable("The conversation categorizer is temporarily unavailable.");
        }
    }
}
