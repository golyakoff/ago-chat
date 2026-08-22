using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
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
/// </summary>
public sealed class SendVisitorMessageHandler(
    IConversationRepository conversations,
    IClock clock,
    IIdGenerator idGenerator,
    IOutboxWriter outbox,
    IRateLimiter rateLimiter,
    MessageSendRateLimitOptions options)
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

        var now = clock.UtcNow;
        var messageId = new MessageId(idGenerator.NewId(now));

        // The participant check lives in Conversation.AddVisitorMessage itself (1-01) - there is
        // nothing left for this handler to verify beforehand, since a visitor holds no role for
        // IPermissionChecker to look up (adr/0016). Reaching either catch here means the caller's
        // own token/conversation pairing was already stale or wrong by the time it got this far.
        try
        {
            var message = conversation.AddVisitorMessage(command.AuthorId, messageId, body, now);

            // adr/0005: staged here, persisted by the same SaveAsync call below - never a separate
            // transaction, so an outbox row can never exist without the message it describes.
            var domainEvent = conversation.DomainEvents.OfType<MessageAdded>().Last();
            outbox.Enqueue(MessageAcceptedMapper.ToEnvelope(domainEvent, idGenerator));
            conversation.ClearDomainEvents();

            await conversations.SaveAsync(conversation, cancellationToken);
            return message.Sequence;
        }
        catch (ConversationParticipantMismatchException)
        {
            return ConversationErrors.Forbidden("This visitor is not a participant of this conversation.");
        }
        catch (InvalidConversationStateException ex)
        {
            return ConversationErrors.InvalidState(ex.Message);
        }
    }
}
