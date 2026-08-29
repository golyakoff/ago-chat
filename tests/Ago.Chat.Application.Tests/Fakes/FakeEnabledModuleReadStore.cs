using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeEnabledModuleReadStore : IEnabledModuleReadStore
{
    private readonly Dictionary<SiteId, List<EnabledModuleSummary>> _bySite = [];

    public void Seed(SiteId siteId, EnabledModuleSummary summary)
    {
        if (!_bySite.TryGetValue(siteId, out var list))
        {
            list = [];
            _bySite[siteId] = list;
        }

        list.Add(summary);
    }

    public Task<IReadOnlyList<EnabledModuleSummary>> GetForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EnabledModuleSummary>>(
            _bySite.TryGetValue(siteId, out var list) ? [.. list] : []);
}
