using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `14-01`: the write-side port for the <see cref="ChannelIdentity"/> aggregate. Shaped by the
/// questions its use cases ask - never a generic <c>IRepository&lt;T&gt;</c> (clean-architecture.md),
/// for the same reason <see cref="IConversationRepository.GetActiveForVisitorAsync"/> exists in that
/// shape.
///
/// <para>Product-specific, so it lives here rather than in <c>Ago.Platform.Abstractions</c>: it names
/// <see cref="Visitor"/> through the identity it returns, and clean-architecture.md's platform test
/// ("can it be described without naming chat, visitors, or operators?") fails on the first
/// sentence.</para>
/// </summary>
public interface IChannelIdentityRepository
{
    /// <summary>
    /// The <b>active</b> identity for this exact (site, channel, address) triple, or
    /// <see langword="null"/> if this address has never been heard from on this site before, or the row
    /// that once existed for it has since been unlinked (`14-12`).
    ///
    /// <para>All three parts are the key, deliberately. Dropping the site would let one tenant's
    /// console resolve a number another tenant has been talking to; dropping the channel would merge a
    /// Telegram id and an SMS number that happen to read alike. `ChannelIdentityConfiguration`'s unique
    /// index is the storage-level backstop for the same triple - the "index is the backstop, not the
    /// primary mechanism" division `adr/0019` draws for <c>messages</c>.</para>
    ///
    /// <para><b>`14-12`: filtered to <see cref="ChannelIdentity.Active"/>, which is also why the unique
    /// index moved from a plain one to a partial one filtered the same way
    /// (<c>ChannelIdentityConfiguration</c>'s own remarks) - once an identity is unlinked, that exact
    /// address resolves as if it had never been heard from, so <c>ReceiveChannelMessageHandler</c>'s "no
    /// existing identity" branch (mint a new visitor, or honour a live pending link code) is exactly
    /// what runs for it next, never a silent reattachment to the visitor it used to point at.</b></para>
    /// </summary>
    Task<ChannelIdentity?> FindAsync(
        SiteId siteId, ChannelKind kind, ExternalChannelAddress address, CancellationToken cancellationToken);

    /// <summary>
    /// `14-02`: the gap this item found in `14-01`'s own port shape, noted rather than worked around
    /// (this item's own Context-to-read-first section). `14-01` shipped only the inbound lookup
    /// (provider address to identity) because it had no outbound caller yet; this item is that caller -
    /// <c>DeliverChannelMessageHandler</c> needs the reverse direction, "does this visitor's conversation
    /// have a channel to relay an operator's reply through, and which one" - to decide whether a
    /// <see cref="Message"/> belongs on any channel at all before it ever asks
    /// <see cref="IInboundChannelAdapterRegistry"/> for an adapter. A <see cref="Visitor"/> can hold more
    /// than one <see cref="ChannelIdentity"/> in principle (`ChannelIdentity`'s own remarks), so this
    /// returns the most recently active one rather than assuming exactly one - the same "most recent
    /// wins" tie-break `IConversationRepository.GetActiveForVisitorAsync` already applies for an
    /// analogous "which one of possibly several" question.
    ///
    /// <para><b>`14-12`: filtered to <see cref="ChannelIdentity.Active"/></b> - an unlinked identity
    /// must never be the channel an operator's reply relays through (`adr/0079` decision 4's "excluded
    /// from routing/preference/lookup"). Proven by a test seeding an unlinked identity as the only row
    /// for a visitor and asserting this returns <see langword="null"/>, not the stale row.</para>
    /// </summary>
    Task<ChannelIdentity?> FindMostRecentForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken);

    /// <summary>`14-12`: every active identity a visitor currently holds, for the console's own
    /// <c>VisitorPanel</c> listing and its "unlink" action per row - the read <c>ChannelIdentity</c> had
    /// no caller for until this item (only "the one most recent" existed before). A plain EF query
    /// through this write-side repository, not a Dapper read store: the same "small, bounded, low-
    /// frequency listing" precedent <c>IConversationRepository.GetWaitingForSiteAsync</c>'s own remarks
    /// already establish for why adr/0004's EF-for-writes/Dapper-for-reads split is a default, not an
    /// absolute - one visitor's own identities are never more than a handful of rows.</summary>
    Task<IReadOnlyList<ChannelIdentity>> ListActiveForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken);

    /// <summary>`14-12`: by primary key, for the two unlink handlers - unlike every other read on this
    /// port, the caller here does not yet know the (site, kind, address) triple, only the id the console
    /// showed. Not filtered to <see cref="ChannelIdentity.Active"/>: an unlink handler must be able to
    /// tell "already unlinked" (idempotent no-op, <c>RevokeChannelCredentialHandler</c>'s own precedent)
    /// apart from "never existed" (a real 404), which requires seeing the row regardless of its current
    /// state.</summary>
    Task<ChannelIdentity?> GetByIdAsync(ChannelIdentityId id, CancellationToken cancellationToken);

    Task SaveAsync(ChannelIdentity identity, CancellationToken cancellationToken);
}
