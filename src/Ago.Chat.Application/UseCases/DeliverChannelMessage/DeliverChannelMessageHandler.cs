using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.DeliverChannelMessage;

/// <summary>
/// `14-02`: turns an operator's already-committed reply into an outbound call through whichever channel
/// the visitor was reached by - `14-01`'s <see cref="IInboundChannelAdapter"/> proven for the first time
/// against a real caller. Driven by <c>Ago.Chat.Worker</c>'s <c>ChannelMessageDeliveryConsumer</c> off
/// the existing <c>MessageAccepted</c> topic - <c>SendOfflineAutoReplyHandler</c>'s own precedent for
/// "why a consumer and not the send path": <see cref="Application.UseCases.SendMessage.SendOperatorMessageHandler"/>
/// is upstream of the write (it only enqueues onto `4-05`'s pipeline), so a relay attempted there would
/// race the write that makes the message real. Reacting to <c>MessageAccepted</c> means the trigger is
/// durable before this handler ever calls out to a third party.
///
/// <para><b>The loop guard.</b> Only <see cref="MessageAuthorKind.Operator"/> is ever relayed. A
/// <see cref="MessageAuthorKind.Visitor"/> message is what arrived <em>from</em> the channel in the
/// first place (`ReceiveChannelMessageHandler`) - relaying it back would echo every inbound MAX message
/// straight back to the same MAX chat. A <see cref="MessageAuthorKind.System"/> message (`14-04`'s
/// offline auto-reply) is deliberately excluded too, and that is a scope line rather than a safety one:
/// this item's own Out-of-scope section leaves "auto-reply's own interaction with this channel" to
/// `14-03`, which is expected to widen this check once it exists.</para>
///
/// <para><b>No new idempotency mechanism.</b> A redelivered <c>MessageAccepted</c> (the broker's own
/// at-least-once) would call <see cref="IInboundChannelAdapter.SendAsync"/> a second time with the
/// identical <see cref="OutboundChannelMessage.MessageId"/> - `resilience.md`'s own stated design
/// (`14-01`): the provider is handed that id as its own idempotency key, so a duplicate delivery is the
/// provider's problem to collapse, not a new dedup table here. This handler makes no Postgres write of
/// its own (unlike <c>SendOfflineAutoReplyHandler</c>, which stages a reply and needs
/// <c>IInboxChecker</c> to keep that write and its outbox row atomic) - there is nothing here for an
/// inbox row to protect.</para>
/// </summary>
public sealed class DeliverChannelMessageHandler(
    IConversationRepository conversations,
    IChannelIdentityRepository identities,
    IInboundChannelAdapterRegistry adapters)
{
    public async Task<DeliverChannelMessageOutcome> HandleAsync(
        DeliverChannelMessage command, CancellationToken cancellationToken)
    {
        // THE LOOP GUARD - first, before any I/O, the same "cost this consumer nothing at all"
        // discipline OfflineAutoReplyConsumer's own remarks describe for its own guard.
        if (command.TriggerAuthorKind != MessageAuthorKind.Operator)
        {
            return DeliverChannelMessageOutcome.NotAnOperatorMessage;
        }

        var conversation = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null)
        {
            // Should not happen: a message exists for this conversation, so the conversation does.
            // Treated as "nothing to relay" rather than a thrown failure - see this handler's own
            // remarks on why every non-Delivered/Refused outcome here is an ack, not a retry candidate.
            return DeliverChannelMessageOutcome.NoLinkedChannel;
        }

        var identity = await identities.FindMostRecentForVisitorAsync(conversation.VisitorId, cancellationToken);
        if (identity is null)
        {
            return DeliverChannelMessageOutcome.NoLinkedChannel;
        }

        var adapter = adapters.For(identity.Kind);
        if (adapter is null)
        {
            return DeliverChannelMessageOutcome.NoAdapterRegistered;
        }

        var trigger = conversation.Messages.FirstOrDefault(m => m.Sequence == command.TriggerSequence);
        if (trigger is null || trigger.AuthorKind != MessageAuthorKind.Operator)
        {
            // The row itself disagrees with the wire field - the same second, row-backed check
            // SendOfflineAutoReplyHandler's own loop guard makes, so this does not depend on
            // MessageAccepted's AuthorKind being trustworthy on its own.
            return DeliverChannelMessageOutcome.NotAnOperatorMessage;
        }

        // Thrown exceptions (transient faults, per IInboundChannelAdapter's own contract) are
        // deliberately not caught here - they propagate to ChannelMessageDeliveryConsumer, which is
        // where messaging.md's retry-then-dead-letter decision belongs, the same split
        // OfflineAutoReplyConsumer already draws between "this handler decides business outcomes" and
        // "the consumer decides what a failure means for redelivery."
        var outcome = await adapter.SendAsync(
            new OutboundChannelMessage(
                identity.Kind, identity.Address, command.ConversationId, command.TriggerMessageId, trigger.Body),
            cancellationToken);

        return outcome.Delivered ? DeliverChannelMessageOutcome.Delivered : DeliverChannelMessageOutcome.Refused;
    }
}
