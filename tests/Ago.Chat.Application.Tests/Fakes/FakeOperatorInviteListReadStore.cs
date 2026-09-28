using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Returns the seeded rows for the requested site (an empty list otherwise) - the same "fake
/// returns canned rows, the handler test proves the mapping/derivation around it" shape
/// <see cref="FakeOperatorTeamReadStore"/> already uses for its own sibling read store over the same
/// table.</summary>
public sealed class FakeOperatorInviteListReadStore : IOperatorInviteListReadStore
{
    private readonly Dictionary<SiteId, List<OperatorInviteListItem>> _bySite = [];

    public SiteId? LastSiteId { get; private set; }

    public void Seed(SiteId siteId, params OperatorInviteListItem[] items) => _bySite[siteId] = [.. items];

    public Task<IReadOnlyList<OperatorInviteListItem>> ListForSiteAsync(SiteId siteId, CancellationToken cancellationToken)
    {
        LastSiteId = siteId;
        IReadOnlyList<OperatorInviteListItem> result = _bySite.TryGetValue(siteId, out var items) ? items : [];
        return Task.FromResult(result);
    }
}
