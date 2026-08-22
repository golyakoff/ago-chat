namespace Ago.Chat.Domain;

/// <summary>
/// The aggregate root for a support conversation: one visitor, at most one assigned operator, its
/// messages, and the <c>Waiting -&gt; Assigned -&gt; Closed</c> state machine that governs both. One
/// aggregate per transaction (data-model.md, adr/0004) - <c>Ago.Chat.Infrastructure.Postgres</c>
/// (`1-04`) persists exactly this shape in one `SaveChangesAsync`.
///
/// <see cref="AssignTo"/> here is a trivial direct assignment - the caller (Application, `1-02`) has
/// already resolved who is allowed to claim it. It is not the queue/capacity-aware assignment engine,
/// which is Stage 4's centerpiece and does not exist yet.
/// </summary>
public sealed class Conversation
{
    private readonly List<Message> _messages = [];
    private readonly List<IDomainEvent> _domainEvents = [];

    public ConversationId Id { get; }

    public SiteId SiteId { get; }

    public VisitorId VisitorId { get; }

    public OperatorId? OperatorId { get; private set; }

    public ConversationState State { get; private set; }

    public int LastSequence { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>Messages the visitor has not yet seen - i.e. authored by the operator.</summary>
    public int VisitorUnreadCount { get; private set; }

    /// <summary>Messages the operator has not yet seen - i.e. authored by the visitor.</summary>
    public int OperatorUnreadCount { get; private set; }

    public IReadOnlyList<Message> Messages => _messages;

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    private Conversation(ConversationId id, SiteId siteId, VisitorId visitorId, DateTimeOffset now)
    {
        Id = id;
        SiteId = siteId;
        VisitorId = visitorId;
        State = ConversationState.Waiting;
        CreatedAt = now;
    }

    // EF Core materialization only (1-04) - every field is overwritten via reflection immediately
    // after construction (including _messages, which EF populates as the Messages navigation), so
    // this never runs Start's logic or raises ConversationStarted for a row that already exists.
    private Conversation()
    {
    }

    public static Conversation Start(ConversationId id, SiteId siteId, VisitorId visitorId, DateTimeOffset now)
    {
        var conversation = new Conversation(id, siteId, visitorId, now);
        conversation._domainEvents.Add(new ConversationStarted(id, siteId, visitorId, now));
        return conversation;
    }

    /// <summary>
    /// Direct-claim by one operator - see the type-level remarks on what this is not.
    ///
    /// `3-03`: a repeat call by the operator who already holds this conversation is a no-op, not an
    /// error - <c>OperatorHub.JoinConversationAsync</c> calls this on every join, including a
    /// reconnect after a dropped connection, and a reconnect is not a new claim. Assigning to a
    /// *different* operator while already assigned is still rejected below - that invariant (one
    /// operator at a time) is exactly what this no-op must not weaken.
    /// </summary>
    public void AssignTo(OperatorId operatorId, DateTimeOffset now)
    {
        if (State == ConversationState.Assigned && OperatorId == operatorId)
        {
            return;
        }

        if (State != ConversationState.Waiting)
        {
            throw new InvalidConversationStateException(
                $"Cannot assign conversation {Id.Value} from state {State}; only {ConversationState.Waiting} can be assigned.");
        }

        OperatorId = operatorId;
        State = ConversationState.Assigned;
        _domainEvents.Add(new ConversationAssigned(Id, operatorId, now));
    }

    /// <summary>
    /// `4-01`: the symmetric opposite of <see cref="AssignTo"/> - takes an `Assigned` conversation
    /// back to `Waiting` so `4-02`'s assignment engine can hand it to someone else. Two real callers
    /// exist in the roadmap for this, neither wired yet: `4-02`'s "claimed the row but no operator
    /// had capacity" path, and `4-04`'s operator-disconnect grace period. Unlike <see cref="AssignTo"/>,
    /// there is no same-operator no-op case to preserve - releasing is never called twice in a row for
    /// the same reason without an assignment in between, so any call while already `Waiting` is a
    /// genuine caller bug, not a redundant retry to tolerate.
    /// </summary>
    public void ReleaseToQueue(DateTimeOffset now)
    {
        if (State != ConversationState.Assigned)
        {
            throw new InvalidConversationStateException(
                $"Cannot release conversation {Id.Value} from state {State}; only {ConversationState.Assigned} can be released.");
        }

        var previousOperatorId = OperatorId!.Value;
        OperatorId = null;
        State = ConversationState.Waiting;
        _domainEvents.Add(new ConversationReleased(Id, previousOperatorId, now));
    }

    public void Close(DateTimeOffset now)
    {
        if (State == ConversationState.Closed)
        {
            throw new InvalidConversationStateException(
                $"Conversation {Id.Value} is already {ConversationState.Closed}.");
        }

        State = ConversationState.Closed;
        _domainEvents.Add(new ConversationClosed(Id, now));
    }

    /// <summary>
    /// The visitor may write while waiting for an operator, or after one is assigned - just never
    /// after the conversation is closed.
    /// </summary>
    public Message AddVisitorMessage(VisitorId authorId, MessageId messageId, MessageBody body, DateTimeOffset now)
    {
        if (authorId != VisitorId)
        {
            throw new ConversationParticipantMismatchException(
                $"Visitor {authorId.Value} is not the visitor of conversation {Id.Value}.");
        }

        if (State == ConversationState.Closed)
        {
            throw new InvalidConversationStateException(
                $"Cannot add a message to closed conversation {Id.Value}.");
        }

        return AddMessage(MessageAuthorKind.Visitor, authorId.Value, messageId, body, now);
    }

    /// <summary>An operator may write only once assigned, and only to their own conversation.</summary>
    public Message AddOperatorMessage(OperatorId authorId, MessageId messageId, MessageBody body, DateTimeOffset now)
    {
        // State first: with no operator assigned yet, "wrong state" is the true cause - checking
        // participant identity first would misreport it as "wrong operator" when there is no
        // operator to be right about.
        if (State != ConversationState.Assigned)
        {
            throw new InvalidConversationStateException(
                $"Cannot add an operator message to conversation {Id.Value} in state {State}; " +
                $"only {ConversationState.Assigned} accepts one.");
        }

        if (authorId != OperatorId!.Value)
        {
            throw new ConversationParticipantMismatchException(
                $"Operator {authorId.Value} is not the assigned operator of conversation {Id.Value}.");
        }

        return AddMessage(MessageAuthorKind.Operator, authorId.Value, messageId, body, now);
    }

    /// <summary>
    /// 2-05: the unread-counter consumer's write, applied against an already-accepted message - the
    /// message itself already passed <see cref="AddVisitorMessage"/>/<see cref="AddOperatorMessage"/>'s
    /// state and participant checks when it was first added, so there is no new invariant to enforce
    /// here, only which side's count moves. No domain event: nothing downstream reacts to an unread
    /// count changing (2-05's backlog item is explicit that exposing it is a separate, later concern).
    /// </summary>
    public void IncrementUnreadCount(MessageAuthorKind authorKind)
    {
        if (authorKind == MessageAuthorKind.Visitor)
        {
            OperatorUnreadCount++;
        }
        else
        {
            VisitorUnreadCount++;
        }
    }

    public void ClearDomainEvents() => _domainEvents.Clear();

    private Message AddMessage(
        MessageAuthorKind authorKind, Guid authorId, MessageId messageId, MessageBody body, DateTimeOffset now)
    {
        LastSequence++;
        var message = new Message(messageId, Id, LastSequence, authorKind, authorId, body, now);
        _messages.Add(message);
        _domainEvents.Add(new MessageAdded(messageId, Id, SiteId, LastSequence, authorKind, now));
        return message;
    }
}
