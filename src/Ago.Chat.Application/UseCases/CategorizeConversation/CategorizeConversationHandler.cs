using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.AiAddOn;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Logging;

namespace Ago.Chat.Application.UseCases.CategorizeConversation;

/// <summary>
/// `19-02`: the system-initiated twin of <see cref="Application.UseCases.TagConversation.TagConversationHandler"/>
/// - same write (<see cref="ITagRepository.AddToConversationAsync"/>), reached by
/// <c>Ago.Chat.Worker.ConversationCategorizationJob</c> instead of an operator's own request. No
/// <see cref="IPermissionChecker"/> call, for the identical reason
/// <see cref="Application.UseCases.AutoCloseConversation.AutoCloseConversationHandler"/>'s own remarks
/// give: nobody is acting on anybody's behalf, so there is no operator whose permission could be
/// checked.
///
/// <para><b>The two hard constraints, enforced here rather than assumed from the prompt.</b> "Never
/// invent a tag": <see cref="ApplyAsync"/> discards any <see cref="TagId"/> the categorizer returns that
/// is not one of the candidates this handler itself sent - the second half of this item's own defence
/// in depth, <see cref="Infrastructure.YandexGpt.YandexGptConversationCategorizerClient"/>'s own remarks
/// are the first half. "Never touch an already-tagged conversation": <see cref="HandleAsync"/> checks
/// <see cref="ITagRepository.GetForConversationAsync"/> before calling the provider at all - a
/// conversation an operator (or an earlier run of this same job) already tagged is skipped, never added
/// to or overwritten, which is also what keeps a second run of this job over the same lookback window
/// idempotent without any separate "already processed" bookkeeping: once a conversation carries even one
/// tag, this handler stops looking at it.</para>
///
/// <para><b>Why <see cref="CategorizationOutcome"/> and not just <see cref="Result"/>.</b> Every path
/// through this handler that reaches the provider (or decides not to) is a legitimate, expected outcome
/// of a background sweep, not a caller error - "this site has no tags configured" and "this conversation
/// is already tagged" are not failures <see cref="Application.UseCases.TagConversation.TagConversationHandler"/>'s
/// own <see cref="ConversationErrors.Forbidden"/>/<see cref="ConversationErrors.TagNotFound"/> shape
/// would fit. <see cref="ConversationCategorizationJob"/> only needs to know whether to count this
/// candidate toward its own "tagged" log line, which <see cref="CategorizationOutcome"/> answers
/// directly.</para>
///
/// <para><b>`25-04`: the add-on gate runs first, and the categorizer is a <see cref="Lazy{T}"/> because
/// of it.</b> This is the background half of "nothing reaches the vendor until a tenant has bought the
/// add-on and turned it on" - and a background sweep is the place where "off" alone is not enough, since
/// a job that already ran must not walk backwards through the archive once a tenant enables it. Injecting
/// <see cref="IConversationCategorizer"/> directly would have meant the container resolved the real
/// YandexGPT-backed implementation - its typed <c>HttpClient</c> and all - on every single candidate of
/// every tenant, including the overwhelming majority who never bought the add-on; the gate would still
/// have refused, but the claim "nothing is even constructed" would have been false, and untestable. A
/// <see cref="Lazy{T}"/> makes the claim structural: <c>Ago.Chat.Application.Tests</c> asserts
/// <see cref="Lazy{T}.IsValueCreated"/> is still <see langword="false"/> after a refused call, which no
/// call-count assertion on an already-constructed fake could ever prove.</para>
/// </summary>
public sealed class CategorizeConversationHandler(
    IConversationReadStore readStore,
    ITagRepository tags,
    Lazy<IConversationCategorizer> categorizer,
    AiProcessingGate aiGate,
    CategorizationOptions options,
    IClock clock,
    ILogger<CategorizeConversationHandler> logger)
{
    public async Task<Result<CategorizationOutcome>> HandleAsync(
        CategorizeConversation command, CancellationToken cancellationToken)
    {
        var conversation = await readStore.GetByIdAsync(command.ConversationId, command.SiteId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        // `25-04`: before anything else, and specifically before `categorizer.Value` is ever touched.
        // The conversation's own CreatedAt is what the cut-off is compared against - not ClosedAt - see
        // AiAddOnEnablement's own remarks for why the stricter of the two tests is the correct one.
        var decision = await aiGate.EvaluateAsync(command.SiteId, conversation.CreatedAt, cancellationToken);
        if (decision != AiProcessingDecision.Allowed)
        {
            return CategorizationOutcome.NotPermitted;
        }

        // Scope's own hard constraint: an operator (or an earlier cycle of this same job) already
        // made this call - never add alongside or overwrite it.
        var existing = await tags.GetForConversationAsync(command.ConversationId, cancellationToken);
        if (existing.Count > 0)
        {
            return CategorizationOutcome.AlreadyTagged;
        }

        // Scope's own hard constraint: a site with no vocabulary gets nothing, silently, not a
        // starter taxonomy invented on its behalf.
        var vocabulary = await tags.GetAllForSiteAsync(command.SiteId, cancellationToken);
        if (vocabulary.Count == 0)
        {
            return CategorizationOutcome.NoTagsConfigured;
        }

        var page = await readStore.GetHistoryAsync(
            command.ConversationId, command.SiteId, beforeSequence: null, options.HistoryMessageCount, cancellationToken);

        // The identical reordering/filtering GenerateReplyDraftHandler's own remarks explain: the read
        // store's own page is newest-first, a categorization prompt needs the exchange in the order it
        // actually happened, and a `14-06` structured-content row is dropped rather than forwarded
        // (`adr/0061`).
        var recentMessages = page.Messages
            .Where(m => m.ContentKind is null && !string.IsNullOrWhiteSpace(m.Body))
            .OrderBy(m => m.Sequence)
            .Select(m => new CategorizationHistoryMessage(
                m.AuthorKind == MessageAuthorKind.Visitor ? CategorizationAuthorKind.Visitor : CategorizationAuthorKind.Operator,
                m.Body))
            .ToList();

        var candidates = vocabulary.Select(t => new CategorizationCandidateTag(t.Id, t.Name)).ToList();

        var result = await categorizer.Value.CategorizeAsync(
            new CategorizationRequest(recentMessages, candidates), cancellationToken);

        return result switch
        {
            CategorizationResult.Success success =>
                await ApplyAsync(command.ConversationId, command.SiteId, success.TagIds, candidates, cancellationToken),
            CategorizationResult.Unavailable => CategorizationOutcome.ProviderUnavailable,
            _ => throw new InvalidOperationException($"Unhandled {nameof(CategorizationResult)} case: {result.GetType()}."),
        };
    }

    private async Task<CategorizationOutcome> ApplyAsync(
        ConversationId conversationId,
        SiteId siteId,
        IReadOnlyList<TagId> returnedTagIds,
        IReadOnlyList<CategorizationCandidateTag> candidates,
        CancellationToken cancellationToken)
    {
        var candidateIds = candidates.Select(c => c.TagId).ToHashSet();
        var applied = 0;

        foreach (var tagId in returnedTagIds.Distinct())
        {
            if (!candidateIds.Contains(tagId))
            {
                // Never reached through YandexGptConversationCategorizerClient's own candidate-name
                // matching (its own remarks), but this port's own contract does not guarantee every
                // implementation honours it - this is the backstop, not decoration.
                logger.LogWarning(
                    "Conversation categorizer returned tag {TagId} that is not in this site's candidate vocabulary; discarded.",
                    tagId.Value);
                continue;
            }

            await tags.AddToConversationAsync(conversationId, siteId, tagId, TagSource.Ai, clock.UtcNow, cancellationToken);
            applied++;
        }

        return applied > 0 ? CategorizationOutcome.Tagged : CategorizationOutcome.NoMatch;
    }
}

/// <summary>Every legitimate outcome one candidate can reach - see this handler's own remarks for why
/// this is not <see cref="Result"/>'s failure channel instead.</summary>
public enum CategorizationOutcome
{
    /// <summary>At least one of the site's own tags was applied.</summary>
    Tagged,

    /// <summary>The provider answered, but judged none of the site's own tags applicable - a real,
    /// valid "no" (<see cref="CategorizationResult.Success"/>'s own remarks), not a failure.</summary>
    NoMatch,

    /// <summary>Skipped: an operator (or an earlier cycle) already tagged this conversation.</summary>
    AlreadyTagged,

    /// <summary>Skipped: this site has no tag vocabulary at all.</summary>
    NoTagsConfigured,

    /// <summary>`25-04`: skipped because this site has not bought the AI add-on, has not enabled it, or
    /// this conversation was created before the tenant's own cut-off. The only outcome on which
    /// <see cref="IConversationCategorizer"/> is never even constructed, let alone called.</summary>
    NotPermitted,

    /// <summary>The provider was unreachable or degraded this cycle - left untouched for the job's own
    /// next cycle to retry, while the conversation is still within its lookback window.</summary>
    ProviderUnavailable,
}
