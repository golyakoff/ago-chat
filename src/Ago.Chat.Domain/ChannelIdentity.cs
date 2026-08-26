namespace Ago.Chat.Domain;

/// <summary>
/// `14-01`: the answer to AGO Inbox's founding question - <em>which external chat-id or phone number
/// corresponds to which <see cref="Visitor"/></em>. One row per (site, channel, external address); the
/// <see cref="VisitorId"/> it points at is the AGO Chat identity every existing use case already knows
/// how to work with, so nothing downstream of this lookup has to learn that channels exist.
///
/// <para><b>Its own aggregate, not a value object on <see cref="Visitor"/>.</b> Three facts decide it,
/// and the backlog item named the first two. (1) One human can hold several at once - the same person
/// messaging a shop by MAX and by SMS is two rows against one <see cref="VisitorId"/>, which an
/// embedded value object cannot express without becoming a collection anyway. (2) The link must survive
/// being unlinked: "this number stopped being this visitor" is a fact worth keeping, and a value object
/// deleted from a parent leaves no trace. (3) The write pattern is resolve-or-create on an inbound
/// message, keyed by columns that are not <see cref="Visitor"/>'s primary key - loading the whole
/// visitor to reach an embedded collection would make every inbound message pay for data it does not
/// use, and would put two aggregates in one transaction for no gain. The alternative - three nullable
/// columns on <c>visitors</c> (kind, address, linked_at) - is the shape that looks cheapest on day one
/// and cannot represent (1) at all.</para>
///
/// <para><b>The identity decision, stated plainly (`adr/0055`).</b> A person reaching a shop by SMS and
/// the same person reaching it through the widget are <b>two <see cref="Visitor"/> rows, not one</b>,
/// and this type is built so that stays true by default. Nothing in either signal proves they are the
/// same human: a widget visitor is a browser holding a token this system signed, an SMS sender is a
/// phone number a carrier attests to, and no shared fact connects them. Merging them on a guess would
/// disclose one channel's conversation history to whoever holds the other - a privacy failure in the
/// direction that actually harms someone, and one that cannot be undone by noticing later. So this type
/// offers no merge, no fuzzy match, and no "same site, similar identifier" heuristic: the only way two
/// channel identities share a visitor is if some future, deliberate, <em>verified</em> linking step says
/// so. That step is not built (nothing asks for it yet), and it needs no schema change when it is -
/// <see cref="VisitorId"/> is a plain column on this table, so a verified link is one UPDATE. The
/// asymmetry is the design: many-to-one is representable today, and the many-to-one edge is only ever
/// created by evidence, never by inference.</para>
///
/// <para><b>No domain event, deliberately.</b> Nothing reacts to a channel identity being linked, and
/// this codebase's own standing rule (see <c>Conversation.MarkReadByOperator</c>'s remarks on the
/// absent visitor-side twin) is that a write path with no reader is not built on speculation. `14-02`'s
/// first real adapter is where a consumer, if one is genuinely needed, will show up.</para>
/// </summary>
public sealed class ChannelIdentity
{
    public ChannelIdentityId Id { get; }

    /// <summary>The tenant, exactly as every other row here is scoped (data-model.md). Two shops
    /// reached from the same phone number are two identities and two visitors - one tenant's history
    /// must never surface in another's console, which is why this is part of the lookup key and not a
    /// derived convenience column.</summary>
    public SiteId SiteId { get; }

    public ChannelKind Kind { get; }

    public ExternalChannelAddress Address { get; }

    /// <summary>The AGO Chat identity this external address resolves to. Settable only through
    /// construction today - see this type's own remarks on why no re-link method exists yet.</summary>
    public VisitorId VisitorId { get; }

    public DateTimeOffset FirstSeenAt { get; }

    public DateTimeOffset LastSeenAt { get; private set; }

    private ChannelIdentity(
        ChannelIdentityId id, SiteId siteId, ChannelKind kind, ExternalChannelAddress address,
        VisitorId visitorId, DateTimeOffset now)
    {
        Id = id;
        SiteId = siteId;
        Kind = kind;
        Address = address;
        VisitorId = visitorId;
        FirstSeenAt = now;
        LastSeenAt = now;
    }

    // EF Core materialization only - every field is overwritten via reflection immediately after
    // construction, the same shape Conversation and Visitor already use.
    private ChannelIdentity()
    {
    }

    /// <summary>
    /// Binds one external address to one <see cref="Visitor"/>, for the first time. Named <c>Link</c>
    /// rather than <c>Create</c> because the interesting act is the binding, not the row: the caller
    /// has already decided <em>which</em> visitor, and that decision - see this type's remarks - is the
    /// one with consequences.
    /// </summary>
    public static ChannelIdentity Link(
        ChannelIdentityId id, SiteId siteId, ChannelKind kind, ExternalChannelAddress address,
        VisitorId visitorId, DateTimeOffset now) =>
        new(id, siteId, kind, address, visitorId, now);

    /// <summary>Records that this address was heard from again - the mirror of
    /// <see cref="Visitor.Touch"/>, and the reason a resolve-or-create writes even on the resolve
    /// path.</summary>
    public void Touch(DateTimeOffset now)
    {
        LastSeenAt = now;
    }
}
