using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IChannelIdentityRepository"/>, keyed the same way
/// <c>ux_channel_identities_site_kind_address</c> is - so a test that would violate the real unique
/// index behaves here the way it would there (the resolve finds the existing row) rather than quietly
/// diverging from production.
/// </summary>
public sealed class FakeChannelIdentityRepository : IChannelIdentityRepository
{
    private readonly Dictionary<(SiteId, ChannelKind, ExternalChannelAddress), ChannelIdentity> _byKey = [];

    public IReadOnlyCollection<ChannelIdentity> All => _byKey.Values;

    public Task<ChannelIdentity?> FindAsync(
        SiteId siteId, ChannelKind kind, ExternalChannelAddress address, CancellationToken cancellationToken) =>
        Task.FromResult(_byKey.GetValueOrDefault((siteId, kind, address)));

    /// <summary>`14-02`: mirrors the real repository's "most recently seen" tie-break -
    /// <c>ChannelIdentityRepository.FindMostRecentForVisitorAsync</c>'s own remarks.</summary>
    public Task<ChannelIdentity?> FindMostRecentForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken) =>
        Task.FromResult(_byKey.Values
            .Where(c => c.VisitorId == visitorId)
            .OrderByDescending(c => c.LastSeenAt)
            .FirstOrDefault());

    public Task SaveAsync(ChannelIdentity identity, CancellationToken cancellationToken)
    {
        _byKey[(identity.SiteId, identity.Kind, identity.Address)] = identity;
        return Task.CompletedTask;
    }
}
