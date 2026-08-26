using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Application.UseCases.GetSiteConfigById;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.SendOfflineAutoReply;

/// <summary>
/// `14-04`: decides whether a visitor's message deserves a scripted automatic reply, and writes one if
/// it does. Driven by <c>Ago.Chat.Worker</c>'s <c>OfflineAutoReplyConsumer</c> off the existing
/// <c>MessageAccepted</c> topic.
///
/// <para><b>Why a consumer and not the send path.</b> The reply must reach every channel, and the one
/// place every channel already converges is the message itself - <c>SendVisitorMessageHandler</c> is
/// upstream of the write (it enqueues onto `4-05`'s pipeline and never touches Postgres), so a reply
/// produced there would be deciding against a conversation state that had not been committed yet.
/// Reacting to <c>MessageAccepted</c> means the trigger is durable before anything looks at it, and it
/// costs nothing extra: the reply travels back out through the same fan-out
/// (<c>ConnectionFanoutConsumer</c>) that already delivers every other message, which is precisely the
/// item's "one mechanism, every channel".</para>
///
/// <para><b>The loop guard, and why it is structural.</b> An automatic reply is an ordinary message, so
/// it publishes <c>MessageAccepted</c> too, and this very consumer sees it. What stops the recursion is
/// that a reply can only be authored <see cref="MessageAuthorKind.System"/>
/// (<see cref="Conversation.AddSystemMessage"/> hardcodes it and takes no author-kind parameter) and
/// this handler acts only on <see cref="MessageAuthorKind.Visitor"/>. The two facts together make a
/// second reply unreachable rather than merely unlikely - there is no ordering, retry or race that
/// produces one, because there is no code path that could. The check is made twice on purpose: once
/// against the event's own <c>AuthorKind</c> (free, before any I/O) and once against the persisted
/// message this handler loads anyway, so the guard does not depend on a wire field being trustworthy.</para>
///
/// <para><b>Idempotency (`CLAUDE.md` rule 5).</b> The reply is staged on the tracked
/// <see cref="Conversation"/> and its outbox row alongside it, and then
/// <see cref="IInboxChecker.TryRecordAndSaveAsync"/> performs the single <c>SaveChangesAsync</c> that
/// commits the message, the outbox row and the <c>(message_id, consumer)</c> dedup row together
/// (`adr/0017`). A redelivery re-stages the same work, loses on that composite key, and persists
/// **nothing** - not the reply, not the outbox row. This handler therefore never calls
/// <c>IConversationRepository.SaveAsync</c>: a second <c>SaveChangesAsync</c> would split the two into
/// different transactions and let a reply survive a duplicate check that was supposed to void it - the
/// exact mistake `adr/0017` warns about, and the same reason <c>RecordUnreadMessageHandler</c> avoids
/// it.</para>
///
/// <para><b>"No operator is available" is three different conditions, and this handler answers two of
/// them deliberately.</b> It replies only when the conversation is still <c>Waiting</c> (nobody has
/// picked it up) <em>and</em> no operator is <c>Online</c> for the site at all. It does **not** reply
/// when every online operator is at capacity: that is a queue wait with a human at the other end, and
/// an auto-reply landing seconds before a real answer is worse than no auto-reply. Those two reads
/// come from Postgres, never from the cache or from Redis presence, because they decide whether to
/// write (`CLAUDE.md` rule 8); only the site's own configuration - which decides *what* to say, not
/// whether a write is safe - is read cache-aside, through
/// <see cref="GetSiteConfigByIdHandler"/>.</para>
///
/// <para><b>Composing another handler</b> is `14-01`'s precedent (<c>ReceiveChannelMessageHandler</c>),
/// used here for the same reason: <see cref="GetSiteConfigByIdHandler"/> already owns this read's
/// cache key, TTL, negative-caching and stampede behaviour, and a second cache-aside read of the same
/// row would be a second thing to invalidate.</para>
/// </summary>
public sealed class SendOfflineAutoReplyHandler(
    GetSiteConfigByIdHandler siteConfig,
    IConversationRepository conversations,
    IOperatorRepository operators,
    IOutboxWriter outbox,
    IInboxChecker inbox,
    IClock clock,
    IIdGenerator idGenerator)
{
    public const string ConsumerName = "offline-auto-reply";

    public async Task<Result<OfflineAutoReplyOutcome>> HandleAsync(
        SendOfflineAutoReply command, CancellationToken cancellationToken)
    {
        // THE LOOP GUARD, first half. Deliberately the very first statement, before any I/O: an
        // auto-reply's own MessageAccepted must cost this consumer nothing at all.
        if (command.TriggerAuthorKind != MessageAuthorKind.Visitor)
        {
            return OfflineAutoReplyOutcome.NotAVisitorMessage;
        }

        // `GetSiteConfigById.GetSiteConfigById`, not a bare `GetSiteConfigById`: the query type shares
        // its simple name with its own namespace's trailing segment, which shadows the import here -
        // `ReceiveChannelMessageHandler`'s `new StartConversation.StartConversation(...)` is the same
        // spelling for the same reason (and see `ResolveMessageDeliveryTargets`' own remarks).
        var site = await siteConfig.HandleAsync(
            new GetSiteConfigById.GetSiteConfigById(command.SiteId), cancellationToken);
        if (site is null)
        {
            // Should not happen: a message exists for this site, so the site does. Reported as a
            // failure rather than a skip so the consumer retries and, eventually, dead-letters -
            // silently ignoring it would hide a real referential problem.
            return ConversationErrors.SiteNotFound(command.SiteId.Value);
        }

        if (!site.OfflineAutoReply.Enabled)
        {
            return OfflineAutoReplyOutcome.Disabled;
        }

        var conversation = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        if (conversation.State != ConversationState.Waiting)
        {
            return OfflineAutoReplyOutcome.ConversationNotWaiting;
        }

        if (await operators.AnyOnlineForSiteAsync(command.SiteId, cancellationToken))
        {
            return OfflineAutoReplyOutcome.OperatorOnline;
        }

        // THE LOOP GUARD, second half - against the row rather than the wire field. Also what supplies
        // the text to match: MessageAccepted carries no body by design, and this aggregate was loaded
        // above anyway (ConversationRepository includes its messages), so this costs no extra read.
        var trigger = conversation.Messages.FirstOrDefault(m => m.Sequence == command.TriggerSequence);
        if (trigger is null || trigger.AuthorKind != MessageAuthorKind.Visitor)
        {
            return OfflineAutoReplyOutcome.NotAVisitorMessage;
        }

        var reply = site.OfflineAutoReply.Match(trigger.Body.Value);
        if (reply is null)
        {
            return OfflineAutoReplyOutcome.NothingToSay;
        }

        var now = clock.UtcNow;
        var messageId = new MessageId(idGenerator.NewId(now));
        // Never MessageBody's own throw path: OfflineAutoReplyRule caps a reply at 1000 characters and
        // refuses an empty one, both well inside MessageBody's own bounds.
        conversation.AddSystemMessage(messageId, new MessageBody(reply), now);

        var domainEvent = conversation.DomainEvents.OfType<MessageAdded>().Last();
        outbox.Enqueue(MessageAcceptedMapper.ToEnvelope(domainEvent, idGenerator));
        conversation.ClearDomainEvents();

        var isFirstDelivery = await inbox.TryRecordAndSaveAsync(
            command.TriggerMessageId, ConsumerName, cancellationToken);
        return isFirstDelivery ? OfflineAutoReplyOutcome.Sent : OfflineAutoReplyOutcome.AlreadyReplied;
    }
}
