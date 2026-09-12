namespace Ago.Chat.Domain;

/// <summary>
/// Anonymous, identified only by a signed token the host issues and validates (realtime.md) - this
/// entity carries no token material itself, only the identity and timestamps that are this system's
/// business, not the host's.
/// </summary>
public sealed class Visitor
{
    public VisitorId Id { get; }

    public SiteId SiteId { get; }

    public DateTimeOffset FirstSeenAt { get; }

    public DateTimeOffset LastSeenAt { get; private set; }

    /// <summary>
    /// `14-13`/`adr/0079` decision 5: a durable, cross-conversation override of "which
    /// <see cref="ChannelIdentity"/> should an operator's next reply go out on" - <see langword="null"/>
    /// until an operator sets one, meaning today's implicit "whichever channel was heard from most
    /// recently" rule still applies. Lives on <see cref="Visitor"/>, not <see cref="Conversation"/>,
    /// because the author's own framing was a durable preference about the person, not a one-off choice
    /// for one conversation (`adr/0079`'s own alternatives-considered section).
    ///
    /// <para><b>Never validated here.</b> This id only has meaning in the context of another aggregate
    /// (<see cref="ChannelIdentity"/>) - whether it names one of *this visitor's own*, currently
    /// <see cref="ChannelIdentity.Active"/> rows is a cross-aggregate question, which is exactly the
    /// kind of check this codebase keeps out of a single aggregate's own invariants (the same split
    /// <c>UnlinkChannelIdentityHandler</c>'s own remarks draw between "the aggregate throws only on a
    /// genuine single-aggregate race" and everything else). <c>SetPreferredChannelIdentityHandler</c>
    /// is where that check happens, before this setter is ever called.</para>
    ///
    /// <para><b>Deliberately never cleared when the identity it names is unlinked - see
    /// <c>DeliverChannelMessageHandler</c>'s own remarks for why read-time tolerance was chosen over a
    /// write-time cleanup.</b> A stale value sitting here is harmless: every reader that matters
    /// (delivery) re-checks <see cref="ChannelIdentity.Active"/> before ever trusting it, the identical
    /// posture `adr/0079` decision 5 states in words ("unset, or pointing at an identity that has since
    /// been unlinked, falls back... unchanged").</para>
    /// </summary>
    public ChannelIdentityId? PreferredChannelIdentityId { get; private set; }

    /// <summary>
    /// `25-56` decisions 1/2/5: one member of <see cref="VisitorEmojiDictionary.Creatures"/>, paired
    /// with <see cref="EmojiFood"/> - an operator-side memory aid, assigned once for this visitor and
    /// never reassigned (see <see cref="AssignEmojiPair"/>'s own remarks for why that invariant lives
    /// there rather than here).
    ///
    /// <para><see langword="null"/> only ever describes a visitor row that predates this item and has
    /// not yet been backfilled - <c>Stage25AddVisitorEmojiPair</c>'s own data migration closes that gap
    /// for every row that already existed, and every row this item's two creation call sites
    /// (<c>StartConversationHandler</c>, <c>ReceiveChannelMessageHandler</c>) write from this point on
    /// calls <see cref="AssignEmojiPair"/> before the first save, so nothing new is ever left null. The
    /// property itself stays nullable rather than becoming a required constructor parameter precisely
    /// so that neither of those true facts needs restating at every one of this type's ~90 existing
    /// call sites across this codebase's tests, almost none of which have any opinion about a visitor's
    /// emoji pair.</para>
    /// </summary>
    public string? EmojiCreature { get; private set; }

    /// <summary>The other half of the pair - see <see cref="EmojiCreature"/>'s own remarks, which this
    /// property shares in full (`25-56` decisions 1/2/5).</summary>
    public string? EmojiFood { get; private set; }

    public Visitor(VisitorId id, SiteId siteId, DateTimeOffset now)
    {
        Id = id;
        SiteId = siteId;
        FirstSeenAt = now;
        LastSeenAt = now;
    }

    /// <summary>
    /// `25-56` decision 5: "assigned once, permanent for that visitor" - the invariant lives here,
    /// guarded by throwing on a second call, rather than as an immutable constructor parameter. A
    /// constructor parameter would make the guarantee airtight by construction, but it would also force
    /// every one of this type's existing callers - including every test that builds a
    /// <see cref="Visitor"/> for a reason that has nothing to do with its emoji pair - to suddenly
    /// supply one. This method costs the same guarantee (nothing outside this type can ever change
    /// <see cref="EmojiCreature"/>/<see cref="EmojiFood"/> once set) for a far smaller blast radius: the
    /// two real call sites that mint a brand-new <see cref="Visitor"/>
    /// (<c>StartConversationHandler</c>, <c>ReceiveChannelMessageHandler</c>) call this immediately
    /// after construction and before the first save, and nothing else in this codebase calls it at all.
    /// </summary>
    public void AssignEmojiPair(string creature, string food)
    {
        if (EmojiCreature is not null || EmojiFood is not null)
        {
            throw new InvalidOperationException(
                $"Visitor {Id.Value} already has an emoji pair - decision 5 is explicit that it is " +
                "assigned once and never reassigned.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(creature);
        ArgumentException.ThrowIfNullOrWhiteSpace(food);

        EmojiCreature = creature;
        EmojiFood = food;
    }

    /// <summary>Records a return visit - the reason history survives a reload (vision.md).</summary>
    public void Touch(DateTimeOffset now)
    {
        LastSeenAt = now;
    }

    /// <summary>
    /// `14-13`: sets (or, given <see langword="null"/>, clears) the preferred reply channel. The caller
    /// - <c>SetPreferredChannelIdentityHandler</c> - has already established that a non-null id names
    /// one of this visitor's own currently-active identities; see this property's own remarks for why
    /// that check cannot live here.
    /// </summary>
    public void SetPreferredChannelIdentity(ChannelIdentityId? channelIdentityId)
    {
        PreferredChannelIdentityId = channelIdentityId;
    }

    // EF Core materialization only (1-04) - never called by domain code.
    private Visitor()
    {
    }
}
