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

    Task SaveAsync(ChannelIdentity identity, CancellationToken cancellationToken);
}
