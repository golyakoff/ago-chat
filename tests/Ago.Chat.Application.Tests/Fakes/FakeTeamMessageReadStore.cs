using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`23-32`: good enough to test <c>ResolveTeamMessageDeliveryTargetsHandler</c> and
/// <c>GetTeamMessageHistoryHandler</c> without a real Postgres query - the identical role every other
/// fake read store in this folder plays for its own real adapter.</summary>
public sealed class FakeTeamMessageReadStore : ITeamMessageReadStore
{
    private readonly Dictionary<SiteId, List<TeamMessageHistoryItem>> _bySite = [];

    public void Seed(SiteId siteId, params TeamMessageHistoryItem[] messages) =>
        _bySite[siteId] = [.. messages];

    public Task<TeamMessageHistoryPage> GetHistoryAsync(
        SiteId siteId, int? beforeSequence, int pageSize, CancellationToken cancellationToken)
    {
        var all = _bySite.TryGetValue(siteId, out var messages) ? messages : [];
        var page = all
            .Where(m => beforeSequence is null || m.Sequence < beforeSequence)
            .OrderByDescending(m => m.Sequence)
            .Take(pageSize)
            .ToList();

        var next = page.Count == pageSize ? page[^1].Sequence : (int?)null;
        return Task.FromResult(new TeamMessageHistoryPage(page, next));
    }

    public Task<IReadOnlyList<TeamMessageHistoryItem>> GetDeltaAsync(
        SiteId siteId, int afterSequence, CancellationToken cancellationToken)
    {
        var all = _bySite.TryGetValue(siteId, out var messages) ? messages : [];
        IReadOnlyList<TeamMessageHistoryItem> result = [.. all.Where(m => m.Sequence > afterSequence).OrderBy(m => m.Sequence)];
        return Task.FromResult(result);
    }

    public Task<TeamMessageHistoryItem?> GetBySequenceAsync(SiteId siteId, int sequence, CancellationToken cancellationToken)
    {
        var all = _bySite.TryGetValue(siteId, out var messages) ? messages : [];
        return Task.FromResult(all.FirstOrDefault(m => m.Sequence == sequence));
    }
}
