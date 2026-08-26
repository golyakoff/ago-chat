using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.SendMessage;

/// <summary>
/// Takes the resolved <see cref="MessageSendRateLimitOptions"/> value directly, not
/// <c>IOptions&lt;MessageSendRateLimitOptions&gt;</c> - Application has never depended on
/// `Microsoft.Extensions.Options` for *how* configuration is bound, only on the plain values that
/// result; the host's DI registration resolves `IOptions&lt;T&gt;.Value` once and hands this handler
/// the POCO, the same way it hands over `IConversationRepository` without Application knowing
/// `AgoChatDbContext` exists.
///
/// `4-05`: this handler no longer writes to Postgres itself - <see cref="Ago.Platform.Kernel"/>'s
/// `IClock`/`IIdGenerator`, `IOutboxWriter`, and `IConversationRepository.SaveAsync` all moved to
/// `Ago.Chat.Api.Pipeline.MessageBatchWriter`, which does the actual mutation+outbox+save off a
/// queue, batched (concurrency.md's "In-process pipeline (Api)"). What stays here is exactly what
/// the backlog's Scope calls "cheap checks, no reason to hold a queue slot for a request that was
/// always going to be rejected": rate limiting and body shape. The conversation load below is *not*
/// new work moved forward - it already ran here before `4-05`, because the per-site rate limit
/// needs `SiteId` and nothing else in this call chain knows it yet. `CLAUDE.md` rule 8 is why this
/// same load can never be reused for the write below: a sequence decision must read fresh, inside
/// the write's own transaction, not from a read a queue-wait earlier.
/// </summary>
public sealed class SendVisitorMessageHandler(
    IConversationRepository conversations,
    IRateLimiter rateLimiter,
    MessageSendRateLimitOptions options,
    IMessagePipeline pipeline)
{
    public async Task<Result<int>> HandleAsync(SendVisitorMessage command, CancellationToken cancellationToken)
    {
        // Per-visitor first, before any database work - cheapest check, and the one most likely to
        // actually be the abuser (caching.md's Goal names both; a misbehaving visitor should not
        // need a conversation lookup to be turned away).
        var visitorLimit = await rateLimiter.CheckAsync(
            new RateLimitKey($"message-send:visitor:{command.AuthorId.Value}"),
            new RateLimitRule(options.PerVisitorCapacity, options.PerVisitorRefillPerSecond),
            cancellationToken);
        if (!visitorLimit.Allowed)
        {
            return ConversationErrors.RateLimited(visitorLimit.RetryAfter);
        }

        var conversation = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        // Per-site second, now that the conversation load has revealed which site - a site under
        // abuse from many visitors should not need every one of them to individually exceed their
        // own bucket first.
        var siteLimit = await rateLimiter.CheckAsync(
            new RateLimitKey($"message-send:site:{conversation.SiteId.Value}"),
            new RateLimitRule(options.PerSiteCapacity, options.PerSiteRefillPerSecond),
            cancellationToken);
        if (!siteLimit.Allowed)
        {
            return ConversationErrors.RateLimited(siteLimit.RetryAfter);
        }

        MessageBody body;
        try
        {
            body = new MessageBody(command.Body);
        }
        catch (ArgumentException ex)
        {
            return ConversationErrors.InvalidBody(ex.Message);
        }

        // The participant/state checks that used to run inline here (AddVisitorMessage's own
        // invariants) still run - just inside the pipeline worker, once it reloads this same
        // conversation. Reaching them there and failing means the caller's own token/conversation
        // pairing was already stale or wrong, exactly as before.
        // `14-06`: validated here, beside the body, and for the same reason - a malformed payload is
        // a caller-fixable rejection, not a fault. Nothing below this line looks inside it.
        var content = StructuredContentBinder.Bind(command.ContentKind, command.Payload, command.Actions);
        if (content.IsFailure)
        {
            return content.Error!.Value;
        }

        var pending = new PendingMessage(
            command.ConversationId, MessageAuthorKind.Visitor, command.AuthorId.Value, body,
            command.AttachmentId, command.ClientMessageId, command.TraceParent, content.Value);
        return await pipeline.EnqueueAsync(pending, cancellationToken);
    }
}
