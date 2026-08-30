using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IChannelIdentityRepository"/>, keyed by <see cref="ChannelIdentityId"/> (a plain
/// dictionary of rows, not one keyed by the triple) - `14-12`: once an identity can be unlinked and a
/// fresh one linked for the identical (site, kind, address) key, keying storage itself on that triple
/// would let the second <see cref="ChannelIdentity.Link"/> silently overwrite the first, now-inactive
/// row instead of coexisting with it the way the real, now-partial <c>ux_channel_identities_site_kind_address_active</c>
/// index allows. <see cref="FindAsync"/> below is what re-derives the "one active row per triple" lookup
/// production actually performs, from this flat store.
/// </summary>
public sealed class FakeChannelIdentityRepository : IChannelIdentityRepository
{
    private readonly Dictionary<ChannelIdentityId, ChannelIdentity> _byId = [];

    public IReadOnlyCollection<ChannelIdentity> All => _byId.Values;

    /// <summary>`14-12`: filtered to <see cref="ChannelIdentity.Active"/> - the port's own remarks.</summary>
    public Task<ChannelIdentity?> FindAsync(
        SiteId siteId, ChannelKind kind, ExternalChannelAddress address, CancellationToken cancellationToken) =>
        Task.FromResult(_byId.Values.FirstOrDefault(
            c => c.SiteId == siteId && c.Kind == kind && c.Address == address && c.Active));

    /// <summary>`14-02`: mirrors the real repository's "most recently seen" tie-break -
    /// <c>ChannelIdentityRepository.FindMostRecentForVisitorAsync</c>'s own remarks. `14-12`: filtered to
    /// <see cref="ChannelIdentity.Active"/>.</summary>
    public Task<ChannelIdentity?> FindMostRecentForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken) =>
        Task.FromResult(_byId.Values
            .Where(c => c.VisitorId == visitorId && c.Active)
            .OrderByDescending(c => c.LastSeenAt)
            .FirstOrDefault());

    /// <summary>`14-12`: see the port's own remarks.</summary>
    public Task<IReadOnlyList<ChannelIdentity>> ListActiveForVisitorAsync(
        VisitorId visitorId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ChannelIdentity>>(_byId.Values
            .Where(c => c.VisitorId == visitorId && c.Active)
            .OrderBy(c => c.FirstSeenAt)
            .ToList());

    /// <summary>`14-12`: see the port's own remarks - not filtered to <see cref="ChannelIdentity.Active"/>.</summary>
    public Task<ChannelIdentity?> GetByIdAsync(ChannelIdentityId id, CancellationToken cancellationToken) =>
        Task.FromResult(_byId.GetValueOrDefault(id));

    public Task SaveAsync(ChannelIdentity identity, CancellationToken cancellationToken)
    {
        _byId[identity.Id] = identity;
        return Task.CompletedTask;
    }
}
