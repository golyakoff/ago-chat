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
///
/// <para><b>`14-12`: the first mutation beyond <see cref="Touch"/>, exactly as this type's own remarks
/// above predicted three items ago ("a verified link is one UPDATE... the many-to-one edge is only
/// ever created by evidence, never by inference").</b> <see cref="Active"/>/<see cref="UnlinkedAt"/> and
/// <see cref="Unlink"/> mirror <see cref="ChannelCredential.Active"/>/<see cref="ChannelCredential.Revoke"/>
/// exactly - the identical "terminal, never a hard delete" shape, for the identical reason: "this number
/// stopped being this visitor" is a fact worth keeping, not erasing (this type's own remarks, point (2)
/// above). <see cref="Link"/> still mints exactly one visitor per new address by default; `14-12`'s real
/// addition is that a caller (<c>ReceiveChannelMessageHandler</c>'s new confirmation branch) may now pass
/// an <em>already-existing</em> <see cref="VisitorId"/> instead of a freshly-minted one, when - and only
/// when - a verified pending link request says so. The unique index on (site, kind, address) is now
/// partial (<c>ChannelIdentityConfiguration</c>'s own remarks) for the identical reason
/// <c>ux_channel_credentials_site_kind_active</c> is: an unlinked identity must never block a fresh link
/// of the same external address to a different (or the same) visitor later.</para>
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
    /// construction - `14-12` still never re-points an existing row at a different visitor; a "wrong
    /// link" is corrected by <see cref="Unlink"/> plus a brand-new row (<see cref="Link"/> again),
    /// never by mutating this field, so the history of who this address believed it was pointing to at
    /// any moment stays intact.</summary>
    public VisitorId VisitorId { get; }

    public DateTimeOffset FirstSeenAt { get; }

    public DateTimeOffset LastSeenAt { get; private set; }

    /// <summary>`14-12`: mirrors <see cref="ChannelCredential.Active"/> exactly. <see langword="true"/>
    /// from <see cref="Link"/> until <see cref="Unlink"/>. Every reader that decides routing, preference
    /// or "which channel is this" must filter to this flag - <c>ChannelIdentityRepository.FindAsync"/>
    /// and <c>FindMostRecentForVisitorAsync</c>, and <c>OperatorAnalyticsReadStore</c>'s own channel
    /// tiebreak - or an unlinked identity keeps silently affecting a decision it no longer should.</summary>
    public bool Active { get; private set; } = true;

    /// <summary><see langword="null"/> until <see cref="Unlink"/> - a boolean flag alone would lose
    /// *when* the state changed, the same reason <see cref="OperatorInvite.RedeemedAt"/> exists
    /// alongside that type's own boolean-shaped <see cref="OperatorInvite.IsRedeemed"/>.</summary>
    public DateTimeOffset? UnlinkedAt { get; private set; }

    private ChannelIdentity(
        ChannelIdentityId id, SiteId siteId, ChannelKind kind, ExternalChannelAddress address,
        VisitorId visitorId, DateTimeOffset now, bool active, DateTimeOffset? unlinkedAt)
    {
        Id = id;
        SiteId = siteId;
        Kind = kind;
        Address = address;
        VisitorId = visitorId;
        FirstSeenAt = now;
        LastSeenAt = now;
        Active = active;
        UnlinkedAt = unlinkedAt;
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
    /// one with consequences. `14-12`: the caller may be handing this either a freshly-minted
    /// <see cref="VisitorId"/> (the ordinary "brand-new address" path, unchanged since `14-01`) or an
    /// existing one a verified pending link request named - this method itself does not need to know
    /// which, because by the time it is called the caller has already decided, and that decision is the
    /// one with consequences (this type's own remarks, again).
    /// </summary>
    public static ChannelIdentity Link(
        ChannelIdentityId id, SiteId siteId, ChannelKind kind, ExternalChannelAddress address,
        VisitorId visitorId, DateTimeOffset now) =>
        new(id, siteId, kind, address, visitorId, now, active: true, unlinkedAt: null);

    /// <summary>Records that this address was heard from again - the mirror of
    /// <see cref="Visitor.Touch"/>, and the reason a resolve-or-create writes even on the resolve
    /// path.</summary>
    public void Touch(DateTimeOffset now)
    {
        LastSeenAt = now;
    }

    /// <summary>
    /// `14-12`: terminal, never a hard delete - <see cref="ChannelCredential.Revoke"/>'s own shape,
    /// including the identical "already revoked/unlinked is a caller bug, not a recoverable state"
    /// treatment. The caller (<c>UnlinkChannelIdentityHandler</c>/<c>UnlinkChannelIdentityAsOwnerHandler</c>)
    /// has already checked <see cref="Active"/> before calling this, the same "handler pre-checks,
    /// aggregate throws only on a genuine race" split <see cref="OperatorInvite.Redeem"/>'s own remarks
    /// describe.
    /// </summary>
    public void Unlink(DateTimeOffset now)
    {
        if (!Active)
        {
            throw new InvalidChannelIdentityStateException($"Channel identity {Id.Value} is already unlinked.");
        }

        Active = false;
        UnlinkedAt = now;
    }
}
