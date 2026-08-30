using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GenerateReplyDraft;

/// <summary>
/// `19-01`: the operator-facing half of `adr/0078`'s kind 1 - checks the same access an operator
/// already needs to send a message on this conversation (<see cref="CreateAttachmentHandler"/>'s own
/// operator branch is the closest existing precedent: permission, then assignment, then a rate limit,
/// then the real work), reads *only this conversation's* own recent history through
/// <see cref="IConversationReadStore"/>, and hands it to <see cref="IReplyDraftGenerator"/> - never a
/// site name, a canned response, another conversation's messages, or anything else the operator did
/// not already have open on screen.
///
/// <para><b>No new permission.</b> `Permission.ConversationSend` already gates "may this operator make
/// this conversation say something" (`CreateAttachmentHandler`, `SendVisitorMessageHandler`'s operator
/// path) - a reply *draft* is not itself a message the visitor will ever see (`adr/0078`'s own "the
/// visitor never sees anything the operator did not choose to send"), but requesting one is still an
/// action inside a conversation an operator must already be allowed to answer, so reusing the existing
/// permission is the correct call rather than inventing `conversation:draft` for a capability that is
/// strictly narrower than what `ConversationSend` already permits.</para>
///
/// <para><b>Why the draft is never itself persisted or sent.</b> There is no write to
/// <see cref="Conversation"/> anywhere in this handler, on purpose - `19-01`'s own "never auto-send"
/// rule is not a check this handler performs, it is a code path that does not exist: the only thing
/// this method can do with a successful draft is return it to the caller, who is `ReplyDraftEndpoints`,
/// whose own response body is read by a browser, not by anything capable of sending a message on the
/// operator's behalf.</para>
/// </summary>
public sealed class GenerateReplyDraftHandler(
    IConversationRepository conversations,
    IConversationReadStore readStore,
    IPermissionChecker permissions,
    IRateLimiter rateLimiter,
    IReplyDraftGenerator generator,
    ReplyDraftOptions options,
    ReplyDraftRateLimitOptions rateLimitOptions)
{
    public async Task<Result<GeneratedReplyDraft>> HandleAsync(
        GenerateReplyDraftAsOperator command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.ConversationSend, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to send messages for this site.");
        }

        var conversation = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        if (conversation.OperatorId != command.RequestedBy)
        {
            return ConversationErrors.Forbidden("This operator is not assigned to this conversation.");
        }

        // Per-operator first, then per-site - `ReplyDraftRateLimitOptions`'s own remarks on why this
        // ordering, mirrored from `CreateAttachmentHandler`.
        var operatorLimit = await rateLimiter.CheckAsync(
            new RateLimitKey($"reply-draft:operator:{command.RequestedBy.Value}"),
            new RateLimitRule(rateLimitOptions.PerOperatorCapacity, rateLimitOptions.PerOperatorRefillPerSecond),
            cancellationToken);
        if (!operatorLimit.Allowed)
        {
            return ConversationErrors.ReplyDraftRateLimited(operatorLimit.RetryAfter);
        }

        var siteLimit = await rateLimiter.CheckAsync(
            new RateLimitKey($"reply-draft:site:{command.SiteId.Value}"),
            new RateLimitRule(rateLimitOptions.PerSiteCapacity, rateLimitOptions.PerSiteRefillPerSecond),
            cancellationToken);
        if (!siteLimit.Allowed)
        {
            return ConversationErrors.ReplyDraftRateLimited(siteLimit.RetryAfter);
        }

        // `beforeSequence: null` - the most recent page, `ConversationHistoryPage`'s own remarks - of
        // exactly this conversation and no other, which is the entire context-minimalism guarantee:
        // there is no second read anywhere in this method that could pull in a different
        // conversation's rows.
        var page = await readStore.GetHistoryAsync(
            command.ConversationId, beforeSequence: null, options.HistoryMessageCount, cancellationToken);

        // The read store returns newest-first (`ConversationHistoryPage`'s own doc comment); a
        // reply-draft prompt needs the exchange in the order it actually happened, so this reverses
        // it. `System`-authored rows (the offline auto-reply, `MessageAuthorKind`'s own remarks) fold
        // into the `Operator` side of the port's two-value vocabulary - it was AGO Chat speaking for
        // the tenant, the same side of the exchange a real operator's own reply belongs on - and a row
        // carrying a `14-06` structured payload instead of a plain body is dropped rather than
        // forwarded: `adr/0061` already decided AGO Chat must not interpret a module's payload, and an
        // LLM prompt is exactly the kind of place a dropped-instead-of-guessed-at payload matters most.
        var recentMessages = page.Messages
            .Where(m => m.ContentKind is null && !string.IsNullOrWhiteSpace(m.Body))
            .OrderBy(m => m.Sequence)
            .Select(m => new ReplyDraftHistoryMessage(
                m.AuthorKind == MessageAuthorKind.Visitor ? ReplyDraftAuthorKind.Visitor : ReplyDraftAuthorKind.Operator,
                m.Body))
            .ToList();

        var result = await generator.GenerateDraftAsync(
            new ReplyDraftGenerationRequest(recentMessages), cancellationToken);

        return result switch
        {
            ReplyDraftGenerationResult.Success success => new GeneratedReplyDraft(success.DraftText),
            ReplyDraftGenerationResult.Unavailable unavailable => ConversationErrors.ReplyDraftUnavailable(unavailable.Reason),
            _ => throw new InvalidOperationException($"Unhandled {nameof(ReplyDraftGenerationResult)} case: {result.GetType()}."),
        };
    }
}

/// <summary>The one thing a caller gets back - deliberately just the text, nothing that could be
/// mistaken for "this was already sent" (no message id, no timestamp, no sequence number - a real
/// message gets all three from `SendMessage`, and a draft is not one).</summary>
public sealed record GeneratedReplyDraft(string DraftText);
