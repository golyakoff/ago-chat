namespace Ago.Chat.Domain;

/// <summary>
/// `23-32`: one message in a tenant's own team chat - "people working the same queue can talk to
/// each other without leaving the console" (the backlog item's own Goal). One room per
/// <see cref="SiteId"/>, every operator of that site a member, no channels and no threads
/// (the item's own Out-of-scope).
///
/// <para><b>Why this constructor is public, unlike <see cref="Message"/>'s <c>internal</c> one.</b>
/// <see cref="Message"/> can only be constructed by <see cref="Conversation"/> because
/// <see cref="Conversation.LastSequence"/> - the invariant that makes a message's own ordering
/// correct - lives on that one in-memory aggregate root, and only the root may hand out the next
/// value. A team chat has no such root: there is no bounded, loadable collection of "every message
/// this room has ever held" (an aggregate that grew forever), and ordering here is enforced by a
/// single atomic database statement instead - the compare-and-set `UPDATE sites SET
/// team_chat_last_sequence = team_chat_last_sequence + 1 ... RETURNING ...`
/// <see cref="Ago.Chat.Application.Abstractions.ITeamChatRepository"/>'s adapter runs inside the same
/// call that persists this row (CLAUDE.md rule 8: "sequences ... come from the database inside the
/// transaction"). Once <see cref="Sequence"/> is known, there is no further invariant left for an
/// aggregate boundary to protect - gating construction behind a fake parent object purely to mirror
/// <see cref="Message"/>'s shape would be the "premature generalisation" clean-architecture.md warns
/// a platform layer against, applied here to a product-level aggregate instead: a pattern reused
/// because it exists, not because this type needs what it buys.</para>
/// </summary>
public sealed class TeamMessage
{
    public TeamMessageId Id { get; }

    public SiteId SiteId { get; }

    public OperatorId AuthorOperatorId { get; }

    /// <summary>
    /// `23-32`'s own "the tenant is visible as the tenant" - stamped once, at send time, from
    /// whether the author held <see cref="Permission.SiteManageOperators"/> for
    /// <see cref="SiteId"/> at that moment (<c>SendTeamMessageHandler</c>). Denormalized rather than
    /// resolved fresh on every read for the same reason <see cref="Message.SiteId"/> and
    /// <see cref="Message.RetentionClass"/> are: a label a reader sees on an old message should
    /// reflect who was speaking at the time, not be silently rewritten by a role change years later -
    /// the account's admin roster is not itself versioned, so "who could grant permissions,
    /// configure the site and erase it, the day this was sent" is a fact only the moment of sending
    /// can answer.
    /// </summary>
    public bool AuthorIsAdmin { get; }

    public MessageBody Body { get; }

    /// <summary>Assigned by the database, not this constructor's caller's choice of order - see this
    /// type's own remarks on why there is no in-memory aggregate to enforce it instead.</summary>
    public int Sequence { get; }

    /// <summary>`5-07`'s retry-dedup, the same wire idiom every other send path in this product
    /// offers - <see langword="null"/> for a caller that never sent one.</summary>
    public Guid? ClientMessageId { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// `23-33`: the tenant's own moderation - <see langword="null"/> for a message nobody has
    /// removed, the moment it was removed otherwise. A tombstone, deliberately not a physical
    /// <c>DELETE</c>: this type's own remarks above already explain why <see cref="Sequence"/> is the
    /// room's real ordering key rather than an incidental detail - a hole punched in it by deleting a
    /// row would misdirect <c>Ago.Chat.Application.Abstractions.ITeamMessageReadStore.GetDeltaAsync</c>'s
    /// own reconnect catch-up (a client resuming after a gap counts on every sequence in between
    /// having existed, not on the survivors alone). The room's own read side
    /// (<c>TeamMessageReadStore</c>) is what actually hides a removed message's <see cref="Body"/>
    /// from every future read - this property only decides whether it does, never touching the
    /// column that holds the original text: `personal-data.md`'s own team-chat row already retains it
    /// forever with no per-tier tiering machinery to safely strip it with, and this item's own concern
    /// is accountability ("who removed what, when"), not scrubbing content nothing asked to scrub.
    /// </summary>
    public DateTimeOffset? RemovedAt { get; private set; }

    public TeamMessage(
        TeamMessageId id, SiteId siteId, OperatorId authorOperatorId, bool authorIsAdmin, MessageBody body,
        int sequence, Guid? clientMessageId, DateTimeOffset createdAt, DateTimeOffset? removedAt = null)
    {
        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "Sequence must be positive.");
        }

        Id = id;
        SiteId = siteId;
        AuthorOperatorId = authorOperatorId;
        AuthorIsAdmin = authorIsAdmin;
        Body = body;
        Sequence = sequence;
        ClientMessageId = clientMessageId;
        CreatedAt = createdAt;
        RemovedAt = removedAt;
    }

    /// <summary>
    /// `23-33`: the account owner's own removal (<c>RemoveTeamMessageHandler</c> - see its own
    /// remarks for exactly who may call this and why). Idempotency is the caller's job, the same
    /// split <see cref="Attachment.MarkDeleted"/>'s own callers already draw
    /// (<c>DeleteAttachmentHandler</c> checks <c>State == Deleted</c> *before* calling it and returns
    /// success without calling this method again) - this method itself still throws on a second call
    /// rather than silently overwriting <see cref="RemovedAt"/>, so a caller that skips its own check
    /// gets a loud bug instead of a quietly wrong timestamp.
    /// </summary>
    public void Remove(DateTimeOffset removedAt)
    {
        if (RemovedAt is not null)
        {
            throw new InvalidOperationException($"TeamMessage {Id.Value} is already removed.");
        }

        RemovedAt = removedAt;
    }
}
