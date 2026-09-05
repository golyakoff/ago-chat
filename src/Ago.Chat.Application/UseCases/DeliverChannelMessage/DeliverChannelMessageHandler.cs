using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

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
///
/// <para><b>`14-13`/`adr/0079` decision 5: the preference is checked first, read-time-tolerant rather
/// than write-time-cleaned.</b> When <see cref="Visitor.PreferredChannelIdentityId"/> is set, this
/// handler loads that exact row by id and uses it only while it is still
/// <see cref="ChannelIdentity.Active"/> - unset, or naming an identity that has since been unlinked,
/// falls through to the unchanged <see cref="IChannelIdentityRepository.FindMostRecentForVisitorAsync"/>
/// rule below, exactly the wording `adr/0079` itself uses ("falls back... when unset or stale").
/// <c>UnlinkChannelIdentityHandler</c>/<c>UnlinkChannelIdentityAsOwnerHandler</c> deliberately do
/// <em>not</em> reach into <see cref="Visitor"/> to null out a preference that pointed at the identity
/// they just unlinked - doing so would put two aggregates (<see cref="ChannelIdentity"/> and
/// <see cref="Visitor"/>) in one transaction for a guarantee this read-time check already provides for
/// free (<see cref="ChannelIdentity"/>'s own remarks on why loading a second aggregate "for no gain" is
/// the shape this codebase avoids). A stale <see cref="Visitor.PreferredChannelIdentityId"/> is
/// harmless: it is never trusted without this same <see cref="ChannelIdentity.Active"/> check, here or
/// anywhere else.</para>
///
/// <para><b>`20-11`: a third, narrower resolution step now runs *ahead of* the preference above -
/// this conversation's own active booking's own priority list, if one was set.</b> `20-11`'s own
/// "Decided" section: "this item's own list, if the visitor added one for this booking; otherwise
/// `14-13`'s own preference; otherwise today's existing most-recent-channel fallback, unchanged." Scoped
/// to <see cref="Conversation.ActiveModuleTask"/> deliberately, not to "any message this handler is ever
/// asked to deliver" - Chat's own <see cref="Domain.ModuleKey"/> is intentionally opaque (no
/// <c>"calendar"</c> literal may appear anywhere in <c>Ago.Chat.*</c>, an arch-tested rule
/// <see cref="Domain.ModuleKey"/>'s own remarks state), so this handler cannot and does not ask "is this
/// module a booking module" - it asks only "does the conversation's current module task have a priority
/// list recorded for it", which is exactly the structural signal `20-11`'s own storage keys on. In
/// practice this list is only ever populated for the one module (`20-07`'s booking flow) whose own
/// console/chat surface offers a way to set it, but nothing in this handler needs to know that.
/// <see cref="ResolveModuleTaskChannelPriorityIdentityAsync"/> tries each entry in the visitor's own priority order
/// and skips a since-unlinked one rather than abandoning the whole list at the first miss, so a lower-
/// ranked but still-verified entry can still win over `14-13`'s preference.</para>
///
/// <para><b>`23-19`: the outcome is now recorded, not only decided.</b> `docs/design/decisions.md` §9 -
/// this handler already received <see cref="ChannelSendOutcome"/> and threw it away; the only change
/// this item makes here is a single <see cref="IChannelDeliveryRepository.SaveAsync"/> call once the
/// provider has actually answered. Only the two terminal branches write a row -
/// <see cref="DeliverChannelMessageOutcome.NoLinkedChannel"/>/<see cref="DeliverChannelMessageOutcome.NoAdapterRegistered"/>/
/// <see cref="DeliverChannelMessageOutcome.NotAnOperatorMessage"/> mean nothing was ever attempted, and
/// this item's own Done-when is explicit that a no-linked-channel conversation "writes nothing at all -
/// the no-linked-channel outcome is not a delivery failure and must not be reported as one." No new
/// transaction, no outbox: this row is not a state change anything else in the system reacts to
/// (`ChannelMessageDeliveryConsumer` keeps deciding ack vs retry exactly as before, off the returned
/// enum, never off this table), so it does not need rule 4's write-then-publish shape - it is closer to
/// <c>UnreadCounterConsumer</c>'s own plain write than to anything that mints an integration
/// event.</para>
/// </summary>
public sealed class DeliverChannelMessageHandler(
    IConversationRepository conversations,
    IChannelIdentityRepository identities,
    IVisitorRepository visitors,
    IModuleTaskChannelPreferenceRepository moduleTaskPreferences,
    IInboundChannelAdapterRegistry adapters,
    IChannelDeliveryRepository deliveries,
    IIdGenerator idGenerator,
    IClock clock)
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

        var identity = await ResolveModuleTaskChannelPriorityIdentityAsync(conversation, cancellationToken)
            ?? await ResolvePreferredIdentityAsync(conversation.VisitorId, cancellationToken)
            ?? await identities.FindMostRecentForVisitorAsync(conversation.VisitorId, cancellationToken);
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

        var now = clock.UtcNow;
        var status = outcome.Delivered ? ChannelDeliveryStatus.Delivered : ChannelDeliveryStatus.Refused;
        var delivery = ChannelDelivery.Record(
            new ChannelDeliveryId(idGenerator.NewId(now)),
            command.SiteId,
            command.ConversationId,
            command.TriggerMessageId,
            identity.Kind,
            identity.Id,
            status,
            outcome.ProviderMessageId,
            outcome.FailureReason,
            now);

        // Insert-only, collapses on MessageId (ChannelDelivery's own remarks) - a redelivered
        // MessageAccepted that reaches this point a second time (the send already having happened and
        // this row already having been written the first time round; see this handler's own class
        // remarks) skips the write rather than growing a second row. Never awaited for its return value
        // - there is no caller-visible difference between "recorded" and "already recorded", both mean
        // the row exists.
        await deliveries.SaveAsync(delivery, cancellationToken);

        return outcome.Delivered ? DeliverChannelMessageOutcome.Delivered : DeliverChannelMessageOutcome.Refused;
    }

    /// <summary>`14-13`: <see langword="null"/> whenever there is no explicit preference to honour -
    /// unset, or naming a row that either does not resolve at all or is no longer
    /// <see cref="ChannelIdentity.Active"/> - so the caller's own <c>??</c> falls through to the
    /// unchanged most-recent rule. Never throws on a missing <see cref="Visitor"/>: that is
    /// <see cref="DeliverChannelMessageHandler"/>'s own "should not happen" case elsewhere in this file,
    /// and this method's contract is "give me a usable preferred identity or nothing", not "prove the
    /// visitor exists".</summary>
    private async Task<ChannelIdentity?> ResolvePreferredIdentityAsync(VisitorId visitorId, CancellationToken cancellationToken)
    {
        var visitor = await visitors.GetByIdAsync(visitorId, cancellationToken);
        if (visitor?.PreferredChannelIdentityId is not { } preferredId)
        {
            return null;
        }

        var preferred = await identities.GetByIdAsync(preferredId, cancellationToken);

        // Active, and still this same visitor's own row - the latter is never expected to fail given
        // SetPreferredChannelIdentityHandler's own write-time check, but costs nothing to confirm here
        // rather than trusting a foreign-key value blindly, the same "the row itself disagrees with the
        // wire field" defensive posture this handler's own loop guard already uses further down.
        return preferred is { Active: true } && preferred.VisitorId == visitorId ? preferred : null;
    }

    /// <summary>`20-11`: <see langword="null"/> when there is no active module task, no priority list was
    /// ever set for it, or every entry in it has since been unlinked - so the caller's own <c>??</c>
    /// falls through to `14-13`'s own preference next. Walks the list in the visitor's own priority order
    /// (<see cref="ModuleTaskChannelPreference.Priority"/> ascending) and returns the first entry whose
    /// <see cref="ChannelIdentity"/> is still <see cref="ChannelIdentity.Active"/> and still belongs to
    /// this same visitor - a since-unlinked top entry does not abandon the whole list, it just yields to
    /// the next-ranked one, honouring the "priority order" the visitor actually set rather than the
    /// coarser "list present or not" this handler could have settled for instead.</summary>
    private async Task<ChannelIdentity?> ResolveModuleTaskChannelPriorityIdentityAsync(
        Conversation conversation, CancellationToken cancellationToken)
    {
        var activeTask = conversation.ActiveModuleTask;
        if (activeTask is null)
        {
            return null;
        }

        var entries = await moduleTaskPreferences.ListForModuleTaskAsync(activeTask.Id, cancellationToken);
        foreach (var entry in entries)
        {
            var candidate = await identities.GetByIdAsync(entry.ChannelIdentityId, cancellationToken);
            if (candidate is { Active: true } && candidate.VisitorId == conversation.VisitorId)
            {
                return candidate;
            }
        }

        return null;
    }
}
