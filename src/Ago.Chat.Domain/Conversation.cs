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

    /// <summary>`20-07`: every module task this conversation has ever held, open or closed - EF's own
    /// navigation, materialized by reflection exactly like <see cref="_messages"/> (this type's own
    /// remarks on the private parameterless constructor). Public access is through
    /// <see cref="ActiveModuleTask"/>; nothing outside this aggregate needs the closed history today,
    /// so there is no public "all tasks" accessor to keep in sync with an invariant nobody reads yet.</summary>
    private readonly List<ModuleTask> _moduleTasks = [];

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

    /// <summary>
    /// `18-10`: what an operator has recorded this conversation as having led to -
    /// <see cref="ConversationOutcome.Unset"/> for every conversation until one explicitly calls
    /// <see cref="SetOutcome"/>. Independent of <see cref="State"/> on purpose: an operator may know
    /// the outcome before or after <see cref="Close"/>, and this item does not make recording one a
    /// precondition of closing (the backlog item's own Scope: forcing it is a UX-friction decision this
    /// item explicitly declines to make unilaterally).
    /// </summary>
    public ConversationOutcome Outcome { get; private set; }

    /// <summary>`18-12`: the four backing fields <see cref="Source"/> is computed from - the same
    /// "private fields, computed public property, EF mapped to the fields by name" shape
    /// <c>Message._contentKind</c>/<c>_payload</c>/<c>_actions</c> already establish for a nullable
    /// multi-column value on this same aggregate (<c>MessageConfiguration</c>'s own remarks). Four
    /// separate nullable strings, not one converted <see cref="TrafficSource"/> column, because
    /// <see cref="TrafficSource"/>'s own fields are independently useful to the report's own
    /// <c>GROUPING SETS</c> (referrer host and UTM campaign are separate grouping dimensions - see
    /// <c>IOperatorAnalyticsReadStore</c>'s remarks) - a single serialized column would force the SQL to
    /// parse it back apart to group on either piece.</summary>
    private string? _trafficReferrerHost;
    private string? _trafficUtmSource;
    private string? _trafficUtmMedium;
    private string? _trafficUtmCampaign;

    /// <summary>
    /// `18-12`: where this conversation's own visitor actually came from - see <see cref="TrafficSource"/>
    /// for the full reasoning (why this lives on <see cref="Conversation"/> and not <see cref="Visitor"/>,
    /// why it is unverified, why nothing here is bucketed). <see langword="null"/> for a conversation the
    /// widget started with no referrer and no UTM tag at all (the common case, not the exception - see
    /// <see cref="TrafficSource.IsEmpty"/>), and, permanently, for every conversation that predates this
    /// column - the migration backfills nothing, the same "null means predates the column, or genuinely
    /// captured nothing, and no reader needs to tell those two apart" shape <see cref="ClosedAt"/> and
    /// <see cref="OperatorLastReadSequence"/> already established on this aggregate.
    ///
    /// <para>Set once, in <see cref="Start"/>, and never mutated afterward - there is no setter method
    /// here the way <see cref="SetOutcome"/> exists for <see cref="Outcome"/>, because nothing about
    /// this item ever asks to revise where a conversation came from after the fact.</para>
    /// </summary>
    public TrafficSource? Source =>
        _trafficReferrerHost is null && _trafficUtmSource is null && _trafficUtmMedium is null && _trafficUtmCampaign is null
            ? null
            : new TrafficSource(_trafficReferrerHost, _trafficUtmSource, _trafficUtmMedium, _trafficUtmCampaign);

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

    /// <summary>`20-07`/`adr/0065` decision 7: "at most one active task per conversation. While it is
    /// active, input goes to the module." <see langword="null"/> for the overwhelming majority of
    /// conversations, which never enter a module task at all.</summary>
    public ModuleTask? ActiveModuleTask => _moduleTasks.FirstOrDefault(t => t.State == ModuleTaskState.Open);

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

    /// <summary>
    /// `18-12`: <paramref name="source"/> is optional and defaults to <see langword="null"/> - every
    /// existing call site before this item (and every existing test) starts a conversation with no
    /// opinion about traffic source at all, the same "optional, defaulting to the honest empty value, so
    /// no existing caller has to be touched" shape <see cref="AddVisitorMessage"/>'s own
    /// <c>retentionClass</c> parameter already established for an unrelated field on this same method
    /// family. An all-empty <see cref="TrafficSource"/> (<see cref="TrafficSource.IsEmpty"/>) is stored
    /// as <see langword="null"/>, not as a value object with four <see langword="null"/> fields inside
    /// it - see <see cref="Source"/>'s own remarks.
    /// </summary>
    public static Conversation Start(
        ConversationId id, SiteId siteId, VisitorId visitorId, DateTimeOffset now, TrafficSource? source = null)
    {
        var conversation = new Conversation(id, siteId, visitorId, now);
        if (source is { IsEmpty: false })
        {
            conversation._trafficReferrerHost = source.ReferrerHost;
            conversation._trafficUtmSource = source.UtmSource;
            conversation._trafficUtmMedium = source.UtmMedium;
            conversation._trafficUtmCampaign = source.UtmCampaign;
        }

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
    /// `18-02`: moves an already-<c>Assigned</c> conversation to a different operator without ever
    /// passing back through <c>Waiting</c> - the visitor's own thread never sees the conversation
    /// leave <c>Assigned</c>, unlike <see cref="ReleaseToQueue"/> immediately below, which is a
    /// genuinely different operation (queue re-entry, not a handoff) and the reason this is a new
    /// method rather than a "release then re-assign" pair of existing calls. <see cref="AssignTo"/>
    /// is not reused either: it only accepts <see cref="ConversationState.Waiting"/>, by design (its
    /// own type-level remarks), so an already-<c>Assigned</c> conversation is exactly the state it
    /// refuses.
    ///
    /// <para><b>Who checks that the caller is really the current operator.</b> Deliberately not this
    /// method - the same split <c>CloseConversationHandler</c>'s own remarks draw for
    /// <see cref="Close"/>: "is this caller the one assigned to this conversation" is a cross-aggregate
    /// permission-shaped fact (adr/0016), not an invariant only the aggregate can see, so
    /// <c>TransferConversationHandler</c> compares <c>conversation.OperatorId</c> against the
    /// command's own <c>FromOperatorId</c> before ever calling this. The alternative - taking
    /// <c>from</c> as a parameter here and throwing on a mismatch, the shape
    /// <see cref="MarkReadByOperator"/> uses - was not taken because <c>MarkReadByOperator</c>'s check
    /// is the read-position invariant itself ("whose watermark is this"), while a transfer's source
    /// check is "may this caller act at all", the same kind of question <c>Close</c> already answers
    /// in its handler rather than its aggregate.</para>
    ///
    /// <para><see cref="HoldsCapacityClaim"/> is carried over unchanged, not consumed and reclaimed -
    /// the whole point of a transfer is that the underlying capacity slot moves with the conversation,
    /// from the departing operator's <c>active_chats</c> to the receiving one's. Whether that requires
    /// a real <c>IOperatorCapacity</c> release-then-claim, or nothing at all because this conversation
    /// was never capacity-tracked to begin with (a hand-picked assignment, `6-09`'s own asymmetry), is
    /// exactly what the flag still says after this call - the handler reads it the same way
    /// <c>CloseConversationHandler</c> reads <see cref="Close"/>'s return value.</para>
    /// </summary>
    /// <returns><see langword="true"/> if this conversation holds a capacity claim the caller must now
    /// move, via <c>IOperatorCapacity</c>, from <see cref="OperatorId"/> (before this call) to
    /// <paramref name="to"/>.</returns>
    public bool TransferTo(OperatorId to, DateTimeOffset now)
    {
        if (State != ConversationState.Assigned)
        {
            throw new InvalidConversationStateException(
                $"Cannot transfer conversation {Id.Value} from state {State}; only {ConversationState.Assigned} can be transferred.");
        }

        var from = OperatorId!.Value;
        var holdsCapacityClaim = HoldsCapacityClaim;
        OperatorId = to;
        _domainEvents.Add(new ConversationTransferred(Id, from, to, now));
        return holdsCapacityClaim;
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
    /// `18-10`: an operator records what this conversation actually led to. No state check against
    /// <see cref="State"/> - unlike every write above this one, this is deliberately callable whether
    /// the conversation is <c>Waiting</c>, <c>Assigned</c> or <c>Closed</c> (<see cref="Outcome"/>'s
    /// own remarks on why it is independent of close).
    ///
    /// <para><b>Rejects <see cref="ConversationOutcome.Unset"/>.</b> That member exists to be the
    /// column's own default, not a value an operator can pick - allowing it back in as an explicit
    /// target would open a "revert to no outcome recorded" path this item's own Scope never asked for
    /// and the console never offers a control for. An operator who wants to change their mind picks a
    /// different real value instead; <see cref="ArgumentOutOfRangeException"/> here is a caller bug
    /// (the Application-layer boundary is where an unrecognised wire value gets turned into a proper
    /// <c>Result</c> failure - <c>SetConversationOutcomeHandler</c>'s own remarks, the same
    /// validate-then-translate split <c>UpdateWidgetConfigHandler</c> already draws for
    /// <c>Locale</c>/<c>Position</c>), not a business rejection this method needs to report as one.</para>
    ///
    /// <para><b>No domain event.</b> Nothing downstream reacts to a conversation's outcome changing -
    /// there is no consumer, no webhook, no capacity claim tied to it, the identical "no domain event:
    /// nothing downstream reacts" reasoning <see cref="IncrementUnreadCount"/>'s own remarks already
    /// give for a different scalar field on this same aggregate. If a real consumer ever needs to know,
    /// that is new scope with its own event, not something to add speculatively here.</para>
    /// </summary>
    public void SetOutcome(ConversationOutcome outcome)
    {
        if (outcome == ConversationOutcome.Unset)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcome), outcome, "An operator can record Converted, NotConverted or FollowUpNeeded - never revert a conversation back to Unset.");
        }

        Outcome = outcome;
    }

    /// <summary>
    /// The visitor may write while waiting for an operator, or after one is assigned - just never
    /// after the conversation is closed.
    ///
    /// <para>`13-06`: <paramref name="retentionClass"/> arrives already resolved by the caller
    /// (<c>MessageBatchWriter</c> reads the owning site's current <see cref="Site.Tier"/> through
    /// `3-04`'s cache, per <see cref="RetentionClass"/>'s own remarks) - this aggregate has no I/O of
    /// its own to resolve it and CLAUDE.md rule 2 forbids it reaching for any. Optional, defaulting
    /// to <see cref="RetentionClass.Free"/>, for the same reason <paramref name="attachmentId"/>/
    /// <paramref name="clientMessageId"/>/<paramref name="content"/> are: dozens of existing test call
    /// sites construct a message with no opinion about retention at all, and forcing every one of
    /// them to supply a class this item's own scope does not concern them with would be exactly the
    /// unscoped blast radius <see cref="Site.Name"/>'s own remarks warn against repeating.</para>
    /// </summary>
    public Message AddVisitorMessage(
        VisitorId authorId, MessageId messageId, MessageBody body, DateTimeOffset now,
        AttachmentId? attachmentId = null, Guid? clientMessageId = null, MessageContent? content = null,
        RetentionClass? retentionClass = null)
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
            MessageAuthorKind.Visitor, authorId.Value, messageId, body, attachmentId, clientMessageId, content, now,
            retentionClass);
    }

    /// <summary>An operator may write only once assigned, and only to their own conversation.</summary>
    public Message AddOperatorMessage(
        OperatorId authorId, MessageId messageId, MessageBody body, DateTimeOffset now,
        AttachmentId? attachmentId = null, Guid? clientMessageId = null, MessageContent? content = null,
        RetentionClass? retentionClass = null)
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
            MessageAuthorKind.Operator, authorId.Value, messageId, body, attachmentId, clientMessageId, content, now,
            retentionClass);
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
    /// <para>Deliberately no <c>attachmentId</c> parameter: nothing has ever needed a system-authored
    /// message with an attachment.</para>
    ///
    /// <para><b>`20-07`: <paramref name="content"/></b>. `14-04`'s original remarks here said a
    /// scripted reply is prose and a content parameter had no caller - that stopped being true the
    /// moment a module task's step needed to reach the conversation as a message: it has no visitor or
    /// operator principal behind it any more than an offline auto-reply does (there is no chat-side
    /// person who "sent" a booking module's confirmation card), so it is <see
    /// cref="MessageAuthorKind.System"/> too, and it carries the same kind/payload/actions shape every
    /// other structured message does (`adr/0061`). <see cref="Body"/> stays mandatory regardless -
    /// <c>RouteConversationToModuleHandler</c> derives it from <see cref="PrimitiveTextRenderer"/>, so
    /// even a module step still renders on a channel with no UI.</para>
    /// </summary>
    public Message AddSystemMessage(
        MessageId messageId, MessageBody body, DateTimeOffset now, Guid? clientMessageId = null,
        RetentionClass? retentionClass = null, MessageContent? content = null)
    {
        if (State == ConversationState.Closed)
        {
            throw new InvalidConversationStateException(
                $"Cannot add a message to closed conversation {Id.Value}.");
        }

        return AddMessage(
            MessageAuthorKind.System, SystemAuthorId, messageId, body, null, clientMessageId, content, now,
            retentionClass);
    }

    /// <summary>
    /// `20-07`/`adr/0065` decision 7: opens the conversation's one allowed active module task. Rejects a
    /// second start while one is already open - the invariant the whole "at most one active task"
    /// principle rests on, enforced here rather than trusted to a caller that checked
    /// <see cref="ActiveModuleTask"/> first and then raced another writer to this same aggregate (the
    /// identical "the aggregate is the only place that can actually enforce its own invariant" reasoning
    /// <see cref="AssignTo"/>'s own remarks give).
    ///
    /// <para><paramref name="id"/> arrives already generated, matching <see cref="AddVisitorMessage"/>'s
    /// own <c>messageId</c> parameter - Domain has no <see cref="Guid.NewGuid"/> of its own
    /// (`CLAUDE.md` rule 2), so the caller (Application, via <c>IIdGenerator</c>) mints it first.</para>
    /// </summary>
    public ModuleTask StartModuleTask(
        ModuleTaskId id, ModuleKey moduleKey, string externalTaskId, DateTimeOffset now,
        MessageContentKind? stepKind, MessagePayload? stepPayload, IReadOnlyList<MessageAction> stepActions)
    {
        if (State == ConversationState.Closed)
        {
            throw new InvalidConversationStateException(
                $"Cannot start a module task on closed conversation {Id.Value}.");
        }

        if (ActiveModuleTask is { } active)
        {
            throw new InvalidConversationStateException(
                $"Conversation {Id.Value} already has an active module task ({active.ModuleKey}); "
                + "only one may be active at a time.");
        }

        var task = new ModuleTask(id, Id, moduleKey, externalTaskId, now, stepKind, stepPayload, stepActions);
        _moduleTasks.Add(task);
        return task;
    }

    /// <summary>Advances the conversation's <see cref="ActiveModuleTask"/> to a new step - the module
    /// answered a reply with more work left to do. Throws if there is no active task: a step can only
    /// ever follow a start, and a caller reaching this without one has already lost track of the
    /// conversation's own state.</summary>
    public void RecordModuleStep(MessageContentKind kind, MessagePayload? payload, IReadOnlyList<MessageAction> actions)
    {
        var active = ActiveModuleTask ?? throw new InvalidConversationStateException(
            $"Conversation {Id.Value} has no active module task to record a step on.");
        active.RecordStep(kind, payload, actions);
    }

    /// <summary>
    /// Closes the conversation's <see cref="ActiveModuleTask"/> - the module reported completion, or the
    /// module proved unreachable and the caller is escalating to a human (backlog item's own "unreachable
    /// module degrades honestly into the escape to an operator"). Both callers reach the identical method:
    /// from this aggregate's own perspective, "the module stopped receiving input" is one fact regardless
    /// of which of the two reasons produced it - the difference is only in what message, if any, the
    /// caller adds alongside this call.
    /// </summary>
    public void CloseModuleTask(DateTimeOffset now)
    {
        var active = ActiveModuleTask ?? throw new InvalidConversationStateException(
            $"Conversation {Id.Value} has no active module task to close.");
        active.Close(now);
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
        AttachmentId? attachmentId, Guid? clientMessageId, MessageContent? content, DateTimeOffset now,
        RetentionClass? retentionClass)
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
            messageId, Id, LastSequence, authorKind, authorId, body, attachmentId, clientMessageId, content, now,
            SiteId, retentionClass ?? RetentionClass.Free);
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
