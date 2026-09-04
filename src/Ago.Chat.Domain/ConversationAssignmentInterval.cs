namespace Ago.Chat.Domain;

/// <summary>
/// `23-03`: an append-only record of one operator holding one conversation, from when to when, and how
/// it came about - `decisions.md` §2's "store ownership intervals, not counters" amendment, in code.
/// Nothing derived is stored here: concurrency at any instant, "additional" versus "standard", average
/// hold time - all of it is a query over these rows, never a column on one.
///
/// <para><b>Its own standalone entity, its own table - not a child collection of <see cref="Conversation"/>,
/// the same split <see cref="ConversationNote"/> already draws for itself and for the identical reason:
/// <c>ConversationRepository.GetByIdAsync</c> loads the whole aggregate on every write path that
/// touches a conversation, and folding a growing append-only history into that load would make every
/// assignment, transfer and close materialize every interval the conversation has ever had, for zero
/// benefit to any of them.</b> It is also not an aggregate in its own right: it has exactly one
/// invariant (<see cref="Close"/> may not run twice) and no lifecycle beyond open-then-close, so unlike
/// <see cref="Conversation"/> it raises no domain event and is reached through no repository shaped
/// around use cases - <c>IConversationAssignmentLog</c>'s own remarks explain why a narrow two-method
/// port is what represents it instead.</para>
///
/// <para><b>Two writes, never a third.</b> <see cref="Open"/> constructs the row a caller then adds to
/// its own unit of work; <see cref="Close"/> stamps <see cref="EndedAt"/> once. Nothing else on this
/// type ever changes after construction - <see cref="Id"/>, <see cref="SiteId"/>,
/// <see cref="ConversationId"/>, <see cref="OperatorId"/>, <see cref="StartedAt"/> and
/// <see cref="Source"/> are as immutable as the fact they record.</para>
/// </summary>
public sealed class ConversationAssignmentInterval
{
    public ConversationAssignmentId Id { get; }

    public SiteId SiteId { get; }

    public ConversationId ConversationId { get; }

    public OperatorId OperatorId { get; }

    public DateTimeOffset StartedAt { get; }

    /// <summary><see langword="null"/> while this operator still holds the conversation - an open
    /// interval is a live one. Stamped exactly once, by <see cref="Close"/>.</summary>
    public DateTimeOffset? EndedAt { get; private set; }

    public ConversationAssignmentSource Source { get; }

    private ConversationAssignmentInterval(
        ConversationAssignmentId id, SiteId siteId, ConversationId conversationId, OperatorId operatorId,
        ConversationAssignmentSource source, DateTimeOffset startedAt)
    {
        Id = id;
        SiteId = siteId;
        ConversationId = conversationId;
        OperatorId = operatorId;
        Source = source;
        StartedAt = startedAt;
    }

    // EF Core materialization only (1-04's precedent) - never called by domain code.
    private ConversationAssignmentInterval()
    {
    }

    /// <summary><paramref name="id"/> arrives already generated (<c>IIdGenerator</c>, at the caller -
    /// Domain has no <see cref="Guid.NewGuid"/> of its own, CLAUDE.md rule 2), the same shape
    /// <see cref="Conversation.StartModuleTask"/>'s own <c>id</c> parameter already establishes.</summary>
    public static ConversationAssignmentInterval Open(
        ConversationAssignmentId id, SiteId siteId, ConversationId conversationId, OperatorId operatorId,
        ConversationAssignmentSource source, DateTimeOffset startedAt) =>
        new(id, siteId, conversationId, operatorId, source, startedAt);

    /// <summary>Stamps the moment this operator stopped holding the conversation. Throws on a second
    /// call - the one invariant this type has, and the reason it is a method rather than a public
    /// setter: "nothing else ever updates it" (`23-03`'s own Scope) has to be true of the row's own
    /// behaviour, not merely of which callers happen to exist today.</summary>
    public void Close(DateTimeOffset endedAt)
    {
        if (EndedAt is not null)
        {
            throw new InvalidOperationException(
                $"Conversation assignment interval {Id.Value} was already closed at {EndedAt:O}.");
        }

        EndedAt = endedAt;
    }
}
