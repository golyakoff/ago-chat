using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Seeded totals per site, standing in for the real Dapper-backed sum over
/// `site_widget_activity` - a site nothing has ever seeded reads back as
/// <see cref="WidgetActivityTotals.None"/>, the same "no row" default the real store's own remarks
/// describe.</summary>
public sealed class FakeWidgetActivityReadStore : IWidgetActivityReadStore
{
    private readonly Dictionary<SiteId, WidgetActivityTotals> _bySite = [];

    public void Seed(SiteId siteId, WidgetActivityTotals totals) => _bySite[siteId] = totals;

    public Task<WidgetActivityTotals> GetTotalsAsync(SiteId siteId, DateOnly since, CancellationToken cancellationToken) =>
        Task.FromResult(_bySite.TryGetValue(siteId, out var existing) ? existing : WidgetActivityTotals.None);
}
