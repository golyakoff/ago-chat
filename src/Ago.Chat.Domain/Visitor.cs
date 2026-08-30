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

    public Visitor(VisitorId id, SiteId siteId, DateTimeOffset now)
    {
        Id = id;
        SiteId = siteId;
        FirstSeenAt = now;
        LastSeenAt = now;
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
