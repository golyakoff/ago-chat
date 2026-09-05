using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`23-22`: seeded per site, the same "good enough to test the handler's own access-check
/// without a real Postgres query" shape <see cref="FakeOperatorAnalyticsReadStore"/> already
/// establishes for a sibling read store over the same table.</summary>
public sealed class FakeOperatorTeamReadStore : IOperatorTeamReadStore
{
    private readonly Dictionary<SiteId, List<OperatorTeamMemberItem>> _bySite = [];

    public SiteId? LastSiteId { get; private set; }

    public void Seed(SiteId siteId, params OperatorTeamMemberItem[] members) =>
        _bySite[siteId] = [.. members];

    public Task<IReadOnlyList<OperatorTeamMemberItem>> GetForSiteAsync(SiteId siteId, CancellationToken cancellationToken)
    {
        LastSiteId = siteId;
        IReadOnlyList<OperatorTeamMemberItem> result = _bySite.TryGetValue(siteId, out var members)
            ? members
            : [];
        return Task.FromResult(result);
    }
}
