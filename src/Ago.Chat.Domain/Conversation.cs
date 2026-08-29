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

    /// <summary>
    /// `18-07`: when <see cref="Close"/> committed - <see langword="null"/> for every conversation
    /// still open, and also, permanently, for one closed before this column existed (the migration
    /// backfills nothing, the same "zero means predates the column" shape
    /// <see cref="OperatorLastReadSequence"/> already established). Added for this item's own
    /// visitor-history summary, which is the first caller that ever needed to say *when* a past
    /// conversation ended rather than only that it had - nothing before this checked more than
    /// <see cref="State"/> itself.
    /// </summary>
    public DateTimeOffset? ClosedAt { get; private set; }

    /// <summary>Messages the visitor has not yet seen - i.e. authored by the operator.</summary>
    public int VisitorUnreadCount { get; private set; }

    /// <summary>Messages the operator has not yet seen - i.e. authored by the visitor.</summary>
    public int OperatorUnreadCount { get; private set; }

    /// <summary>
    /// `5-15`: the highest <see cref="Message.Sequence"/> the assigned operator has actually seen -
    /// the watermark that makes <see cref="OperatorUnreadCount"/> clearable without losing a message
    /// that arrives in the same instant. Zero means "nothing read yet", which is also what every row
    /// that predates this column says (the migration's default), so an operator's first open after
    /// the upgrade clears the whole accumulated backlog rather than inheriting a wrong watermark.
    ///
    /// There is deliberately no visitor-side twin: see <see cref="MarkReadByOperator"/>'s remarks.
    /// </summary>
    public int OperatorLastReadSequence { get; private set; }

    /// <summary>
    /// `6-09`: whether this conversation's current assignment is backed by a real
    /// <c>operators.active_chats</c> slot, taken by the automatic assignment engine's atomic
    /// compare-and-set (<c>IOperatorCapacity.TryClaimAsync</c>). It is the receipt for that claim,
    /// and it exists because the two ways a conversation becomes `Assigned` are genuinely not
    /// symmetric: the engine claims a slot first and assigns second, while an operator picking a
    /// conversation up by hand (<c>AssignConversationHandler</c>, behind
    /// <c>OperatorHub.JoinConversationAsync</c>) never touches capacity at all. Without a receipt,
    /// "release the claim when this conversation stops being assigned" has no way to tell a slot that
    /// was taken from one that never was, and would decrement someone else's slot for every
    /// hand-picked conversation ever closed - an under-count that lets the engine over-subscribe an
    /// operator, which is a worse bug than the leak this flag exists to fix.
    ///
    /// <para>Unlike <c>operators.active_chats</c> - a shadow property precisely so no EF
    /// load-mutate-save can ever race the raw <c>UPDATE</c> that owns it - this is an ordinary mapped
    /// property, because it has exactly one writer: this aggregate, saved under the row's own `xmin`.
    /// That single-writer fact is also what makes the release idempotent, see <see cref="Close"/>.</para>
    /// </summary>
    public bool HoldsCapacityClaim { get; private set; }

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
    ///
    /// <para>`6-09`: <paramref name="holdsCapacityClaim"/> defaults to <see langword="false"/>, and
    /// the default is the safe direction rather than the common one - "this assignment is not backed
    /// by a capacity slot" can only ever under-release, never decrement a slot somebody else holds.
    /// Only a caller that has just watched <c>IOperatorCapacity.TryClaimAsync</c> return
    /// <see langword="true"/>, in the same transaction as this save, may pass <see langword="true"/>
    /// (`4-02`/`4-03`'s two <c>IAssignmentClaimer</c> implementations - nothing else).</para>
    ///
    /// <para>The reconnect no-op above returns *before* touching the flag, deliberately: an
    /// engine-assigned conversation whose operator then re-joins it through the hub must keep its
    /// receipt, or the join would silently convert a real claim into an unaccounted one.</para>
    /// </summary>
    public void AssignTo(OperatorId operatorId, DateTimeOffset now, bool holdsCapacityClaim = false)
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
        HoldsCapacityClaim = holdsCapacityClaim;
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
    /// <returns>`6-09`: whether this transition consumed a capacity claim - see <see cref="Close"/>.</returns>
    public bool ReleaseToQueue(DateTimeOffset now)
    {
        if (State != ConversationState.Assigned)
        {
            throw new InvalidConversationStateException(
                $"Cannot release conversation {Id.Value} from state {State}; only {ConversationState.Assigned} can be released.");
        }

        var previousOperatorId = OperatorId!.Value;
        var claimConsumed = HoldsCapacityClaim;
        HoldsCapacityClaim = false;
        OperatorId = null;
        State = ConversationState.Waiting;
        _domainEvents.Add(new ConversationReleased(Id, previousOperatorId, now));
        return claimConsumed;
    }

    /// <summary>
    /// `6-09`: closing is one of the two ways an assignment ends (<see cref="ReleaseToQueue"/> is the
    /// other), so it is also one of the two places a capacity claim has to be handed back.
    ///
    /// <para><b>Why the answer is returned rather than left for the caller to look up.</b> The caller
    /// must release exactly when this call is the one that consumed the claim. Reading
    /// <see cref="HoldsCapacityClaim"/> before calling would be a check-then-act on a value this
    /// method is about to change, and it would still be true on an aggregate whose close then threw.
    /// Returning it makes "a claim was consumed here" and "the claim is now gone from the aggregate"
    /// the same indivisible step.</para>
    ///
    /// <para><b>Why that makes the release idempotent without a dedup flag of its own.</b> Clearing
    /// the receipt is part of the very same <c>SaveChangesAsync</c> as the state transition, under the
    /// conversation row's own `xmin` (adr/0004, `6-08`). Two closes racing means one wins and the
    /// other's save is rejected outright; a close retried after a conflict (`6-08`'s retry-once)
    /// re-reads the row, and a row that is already `Closed` throws below and never reaches a release
    /// at all. There is no interleaving in which one close's claim is released twice, and none in
    /// which a release happens without a claim having existed - which is precisely why this is a
    /// state transition and not a "have I released yet?" boolean the caller checks.</para>
    /// </summary>
    /// <returns><see langword="true"/> if this close consumed a capacity claim the caller must now
    /// release through <c>IOperatorCapacity.ReleaseAsync</c>, for
    /// <see cref="OperatorId"/>.</returns>
    public bool Close(DateTimeOffset now)
    {
        if (State == ConversationState.Closed)
        {
            throw new InvalidConversationStateException(
                $"Conversation {Id.Value} is already {ConversationState.Closed}.");
        }

        var claimConsumed = HoldsCapacityClaim;
        HoldsCapacityClaim = false;
        State = ConversationState.Closed;
        ClosedAt = now;
        _domainEvents.Add(new ConversationClosed(Id, now));
        return claimConsumed;
    }

    /// <summary>
    /// The visitor may write while waiting for an operator, or after one is assigned - just never
    /// after the conversation is closed.
    /// </summary>
    public Message AddVisitorMessage(
        VisitorId authorId, MessageId messageId, MessageBody body, DateTimeOffset now,
        AttachmentId? attachmentId = null, Guid? clientMessageId = null, MessageContent? content = null)
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

        return AddMessage(
            MessageAuthorKind.Visitor, authorId.Value, messageId, body, attachmentId, clientMessageId, content, now);
    }

    /// <summary>An operator may write only once assigned, and only to their own conversation.</summary>
    public Message AddOperatorMessage(
        OperatorId authorId, MessageId messageId, MessageBody body, DateTimeOffset now,
        AttachmentId? attachmentId = null, Guid? clientMessageId = null, MessageContent? content = null)
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

        return AddMessage(
            MessageAuthorKind.Operator, authorId.Value, messageId, body, attachmentId, clientMessageId, content, now);
    }

    /// <summary>
    /// `14-04`: a message AGO Chat itself authored on the tenant's behalf - today the offline
    /// auto-reply and nothing else. There is no participant to check, because there is no principal:
    /// <see cref="SystemAuthorId"/> is the author, and the only state that can refuse one is
    /// <see cref="ConversationState.Closed"/>, exactly as for <see cref="AddVisitorMessage"/>.
    ///
    /// <para><b>This method is where the auto-reply's loop guard is actually enforced.</b> It hardcodes
    /// <see cref="MessageAuthorKind.System"/> and takes no author kind from the caller, so no
    /// auto-reply can ever be recorded as a visitor message - and the consumer that produces
    /// auto-replies acts only on visitor messages. Whether the reply can trigger another reply is
    /// therefore a property of these two facts together, not of any runtime check that could be
    /// skipped, mis-ordered or retried around. See <see cref="MessageAuthorKind.System"/>'s own
    /// remarks.</para>
    ///
    /// <para>Deliberately no <c>attachmentId</c> and no <c>content</c> parameter: a scripted reply is
    /// prose, and a parameter with no caller is a guess about the second one.</para>
    /// </summary>
    public Message AddSystemMessage(
        MessageId messageId, MessageBody body, DateTimeOffset now, Guid? clientMessageId = null)
    {
        if (State == ConversationState.Closed)
        {
            throw new InvalidConversationStateException(
                $"Cannot add a message to closed conversation {Id.Value}.");
        }

        return AddMessage(
            MessageAuthorKind.System, SystemAuthorId, messageId, body, null, clientMessageId, null, now);
    }

    /// <summary>`14-04`: the <see cref="Message.AuthorId"/> every
    /// <see cref="MessageAuthorKind.System"/> message carries. <see cref="Guid.Empty"/> because it is
    /// the honest value - there is no principal behind a scripted reply, and inventing a synthetic
    /// operator id would put a row-shaped lie in the one column a reviewer would use to find out who
    /// said something.</summary>
    public static readonly Guid SystemAuthorId = Guid.Empty;

    /// <summary>
    /// 2-05: the unread-counter consumer's write, applied against an already-accepted message - the
    /// message itself already passed <see cref="AddVisitorMessage"/>/<see cref="AddOperatorMessage"/>'s
    /// state and participant checks when it was first added, so there is no new invariant to enforce
    /// here, only which side's count moves. No domain event: nothing downstream reacts to an unread
    /// count changing (2-05's backlog item is explicit that exposing it is a separate, later concern).
    ///
    /// `5-15`: <paramref name="sequence"/> is what lets this increment and
    /// <see cref="MarkReadByOperator"/> compose instead of fight. The consumer runs in
    /// <c>Ago.Chat.Worker</c> and the mark-read runs in <c>Ago.Chat.Api</c>, so "message accepted"
    /// and "operator read up to here" genuinely race; the row's `xmin` makes one of the two saves
    /// lose and reload, but reloading is only useful if the losing operation can re-decide correctly
    /// against fresh data. Guarding on the watermark is that re-decision: a message at or below what
    /// the operator has already seen was seen, whichever order the two writes land in, and a message
    /// above it is counted whenever its increment arrives - including after the mark-read already
    /// committed, which is exactly the "arrived and was never seen must still be counted" case.
    ///
    /// The visitor side has no watermark to consult, so it always increments, exactly as before -
    /// see <see cref="MarkReadByOperator"/> for why that half is deliberately left alone.
    ///
    /// `14-04`: <see cref="MessageAuthorKind.System"/> lands in that same visitor-side branch, and
    /// that is correct rather than incidental - an auto-reply is something the visitor has not read
    /// yet, and it is emphatically not something the operator needs a badge for.
    /// </summary>
    public void IncrementUnreadCount(MessageAuthorKind authorKind, int sequence)
    {
        if (authorKind == MessageAuthorKind.Visitor)
        {
            if (sequence <= OperatorLastReadSequence)
            {
                return;
            }

            OperatorUnreadCount++;
        }
        else
        {
            VisitorUnreadCount++;
        }
    }

    /// <summary>
    /// `5-15`: the counter's missing other half - until this existed, <see cref="OperatorUnreadCount"/>
    /// only ever went up, so an operator's badge meant "visitor messages this conversation has ever
    /// contained", not "messages you have not read".
    ///
    /// <para><b>Up to a sequence, never an unconditional zero.</b> <paramref name="upToSequence"/> is
    /// the newest message the operator actually has in front of them, so this clears exactly what they
    /// saw. Zeroing instead would silently swallow a visitor message that lands in the same instant:
    /// load-reset-save re-runs "set it to zero" on reload after an `xmin` conflict and throws away the
    /// concurrent increment. Clearing to a watermark is idempotent under any interleaving with
    /// <see cref="IncrementUnreadCount"/>, which is what makes the retry in
    /// <c>MarkConversationReadHandler</c> safe rather than lossy.</para>
    ///
    /// <para>Clamped to <see cref="LastSequence"/>: a client claiming to have read into the future
    /// must not be able to suppress messages that do not exist yet.</para>
    ///
    /// <para>The count is decremented by the visitor messages inside the newly-read range rather than
    /// recomputed from <see cref="_messages"/>, because the two writers disagree about *when* a
    /// message counts: the message row commits first and its increment lands later, asynchronously
    /// (adr/0005's outbox, then `2-05`'s consumer), so a recompute would double-count every message
    /// whose increment is still in flight. <c>Math.Max(0, ...)</c> is the price of that split: when
    /// the consumer is lagging, the range holds messages that were never counted, and the subtraction
    /// would otherwise go negative. The residual is a bounded, self-healing under-count in one exotic
    /// interleaving - increments for the same conversation arriving out of order across the *read
    /// boundary* (a higher sequence counted while a lower one is still in flight) - and it clears on
    /// the operator's next read. It is never an over-count, and never drops a message the operator has
    /// not seen: everything above the watermark is still counted when its increment lands.</para>
    ///
    /// <para>Returns whether anything changed, so the caller can skip the save entirely for the
    /// no-op case. That matters more than it looks: the console calls mark-read on every open,
    /// including re-opens of a conversation that is already read, and a save that writes nothing
    /// would still bump `xmin` and make a genuinely concurrent writer lose for no reason.</para>
    ///
    /// <para><b>Operator side only, deliberately.</b> <see cref="VisitorUnreadCount"/> has the
    /// identical never-cleared shape and is left exactly as it was: nothing reads it. The widget
    /// renders no unread badge, so a `MarkReadByVisitor` would be a write path with no caller, no
    /// transport, and no way to prove it works end to end - the same reason `messages.read_at` has
    /// stayed a column with no writer. When the widget grows a badge, the shape here transfers
    /// unchanged: a `VisitorLastReadSequence` twin plus the mirrored guard in
    /// <see cref="IncrementUnreadCount"/>.</para>
    /// </summary>
    public bool MarkReadByOperator(OperatorId operatorId, int upToSequence)
    {
        // Checked here rather than in the handler (unlike Close, which takes no OperatorId to check
        // against) - the same shape AddOperatorMessage already uses. "Whose read position is this"
        // is a fact about the conversation, not a permission (adr/0016), and the aggregate is the
        // only place that can answer it.
        if (OperatorId is null || operatorId != OperatorId.Value)
        {
            throw new ConversationParticipantMismatchException(
                $"Operator {operatorId.Value} is not the assigned operator of conversation {Id.Value}.");
        }

        var readUpTo = Math.Min(upToSequence, LastSequence);
        if (readUpTo <= OperatorLastReadSequence)
        {
            return false;
        }

        var newlyRead = _messages.Count(m =>
            m.AuthorKind == MessageAuthorKind.Visitor &&
            m.Sequence > OperatorLastReadSequence &&
            m.Sequence <= readUpTo);

        OperatorLastReadSequence = readUpTo;
        OperatorUnreadCount = Math.Max(0, OperatorUnreadCount - newlyRead);
        return true;
    }

    public void ClearDomainEvents() => _domainEvents.Clear();

    /// <summary>
    /// `5-07`: <paramref name="clientMessageId"/> retry-dedup, the same no-op-on-repeat shape
    /// <see cref="AssignTo"/> already established for "the caller already got what they asked for" -
    /// a message retried after a dropped connection (realtime.md's Client protocol section:
    /// "a retried send after a flaky reconnect must not create a second message") returns the
    /// *original* <see cref="Message"/> unchanged, burning no new <see cref="Sequence"/> and raising
    /// no second <see cref="MessageAdded"/>. Checked against <see cref="_messages"/> - already fully
    /// loaded here (<c>ConversationRepository.GetByIdAsync</c>'s <c>Include("_messages")</c>), so this
    /// costs nothing extra to look up and, unlike a database-only unique index, also catches a
    /// duplicate arriving twice within the same in-process batch
    /// (<c>MessageBatchWriter.FlushAsync</c>'s own multi-item loop) before either ever reaches SQL.
    /// A database-level unique index still backs this up (`MessageConfiguration`) for the case this
    /// in-memory check cannot see - two different processes racing the same retry concurrently, each
    /// with its own freshly-loaded copy of this aggregate - matching adr/0019's own "the index is the
    /// storage backstop, not the primary mechanism" reasoning for the neighbouring
    /// <c>(conversation_id, sequence, created_at)</c> index. <see langword="null"/>
    /// <paramref name="clientMessageId"/> (a caller that never sent one) always skips this check -
    /// there is nothing to deduplicate against.
    /// </summary>
    private Message AddMessage(
        MessageAuthorKind authorKind, Guid authorId, MessageId messageId, MessageBody body,
        AttachmentId? attachmentId, Guid? clientMessageId, MessageContent? content, DateTimeOffset now)
    {
        if (clientMessageId is { } id)
        {
            var existing = _messages.FirstOrDefault(m => m.ClientMessageId == id);
            if (existing is not null)
            {
                return existing;
            }
        }

        LastSequence++;
        var message = new Message(
            messageId, Id, LastSequence, authorKind, authorId, body, attachmentId, clientMessageId, content, now, SiteId);
        _messages.Add(message);

        // `14-06`: MessageAdded gains nothing. The integration event it maps to (MessageAccepted)
        // deliberately carries no body already - "a consumer that needs it reads
        // GetConversationHistory instead" - and a payload AGO Chat cannot interpret is the last thing
        // that should travel on a topic other products' consumers read. Structured content reaches a
        // client the same way prose does: through the delivery path, from the row.
        _domainEvents.Add(new MessageAdded(messageId, Id, SiteId, LastSequence, authorKind, now));
        return message;
    }
}
