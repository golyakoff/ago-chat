using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `14-01`: the write-side port for the <see cref="ChannelIdentity"/> aggregate. Shaped by the one
/// question its only use case asks - "who is this external address?" - never a generic
/// <c>IRepository&lt;T&gt;</c> (clean-architecture.md), for the same reason
/// <see cref="IConversationRepository.GetActiveForVisitorAsync"/> exists in that shape.
///
/// <para>Product-specific, so it lives here rather than in <c>Ago.Platform.Abstractions</c>: it names
/// <see cref="Visitor"/> through the identity it returns, and clean-architecture.md's platform test
/// ("can it be described without naming chat, visitors, or operators?") fails on the first
/// sentence.</para>
/// </summary>
public interface IChannelIdentityRepository
{
    /// <summary>
    /// The identity for this exact (site, channel, address) triple, or <see langword="null"/> if this
    /// address has never been heard from on this site before.
    ///
    /// <para>All three parts are the key, deliberately. Dropping the site would let one tenant's
    /// console resolve a number another tenant has been talking to; dropping the channel would merge a
    /// Telegram id and an SMS number that happen to read alike. `ChannelIdentityConfiguration`'s unique
    /// index is the storage-level backstop for the same triple - the "index is the backstop, not the
    /// primary mechanism" division `adr/0019` draws for <c>messages</c>.</para>
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
    /// </summary>
    Task<ChannelIdentity?> FindMostRecentForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken);

    Task SaveAsync(ChannelIdentity identity, CancellationToken cancellationToken);
}
