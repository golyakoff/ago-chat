using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// `18-02`: deliberately maps <see cref="ConversationTransferred"/> onto the <em>existing</em>
/// <see cref="ConversationAssignedToOperator"/> wire contract rather than introducing a new one -
/// the fact this event announces ("this conversation now has this assigned operator") is exactly what
/// that contract already says, and every consumer of it already treats "an assignment happened"
/// generically:
/// <c>Ago.Chat.Worker.ConversationAssignmentFanoutConsumer</c> pushes the SignalR
/// <c>ConversationAssigned</c> event to both the visitor and the newly-assigned operator
/// (<c>ResolveConversationAssignmentTargetsHandler</c>), and
/// <c>Ago.Chat.Webhooks.ConversationAssignmentWebhookDispatchConsumer</c> fires the
/// <c>conversation.assigned</c> webhook. A transfer reaching both of those with zero new consumer code
/// is this item's own stated decision for "what the visitor sees" (backlog `18-02`'s own open call):
/// the operator identity the visitor's widget displays updates live, the same push an initial
/// assignment produces, and nothing else is announced in the conversation thread - no system message,
/// no separate "you have been transferred" signal. Inventing a distinct
/// <c>ConversationTransferredToOperator</c> contract plus two new `Competing` consumers was rejected as
/// scope the backlog item's own Scope section never asks for ("both participants are told" is
/// satisfied by the existing fan-out, not a reason to duplicate it).
///
/// <para><see cref="ConversationTransferred.FromOperatorId"/> does not travel on the wire - no
/// consumer today needs to know who a conversation moved *from*, only who it is with now. It stays on
/// the domain event (see that type's own remarks) for the day one does.</para>
/// </summary>
public static class ConversationTransferredMapper
{
    public static EventEnvelope ToEnvelope(
        ConversationTransferred domainEvent, SiteId siteId, VisitorId visitorId, IIdGenerator idGenerator)
    {
        // Same reasoning as ConversationAssignedToOperatorMapper: two independently-generated ids, not
        // one reused for both - MessageId is this outboxed event's own identity (and, unlike
        // ConversationClosedMapper's reuse of the conversation id, a conversation can be transferred
        // more than once, so its own id would collide on the outbox's primary key the second time -
        // see `6-10`'s own "Found in passing" note on exactly that failure mode for a different mapper).
        var eventId = idGenerator.NewId(domainEvent.OccurredAt);
        var contract = new ConversationAssignedToOperator(
            ConversationId: domainEvent.ConversationId.Value,
            SiteId: siteId.Value,
            VisitorId: visitorId.Value,
            OperatorId: domainEvent.ToOperatorId.Value,
            CorrelationId: idGenerator.NewId(domainEvent.OccurredAt),
            OccurredAt: domainEvent.OccurredAt);

        return new EventEnvelope(
            MessageId: eventId,
            Type: nameof(ConversationAssignedToOperator),
            Version: 1,
            PartitionKey: contract.ConversationId.ToString(),
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
