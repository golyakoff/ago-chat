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

    // `now` is part of the port's own real filtering contract (an expired grant's row is excluded
    // by the real, SQL-backed store), but nothing this fake seeds ever carries an expiry - a unit
    // test exercising a handler through this fake is never testing expiry filtering itself, which
    // is a Ago.Chat.Infrastructure.Postgres concern proven against real Postgres instead
    // (Ago.Chat.Integration.Tests). Accepted and ignored, the same "unused but present for interface
    // parity" shape this codebase's other fakes already use for a parameter their own scenario never
    // varies.
    public Task<IReadOnlyList<EnabledModuleSummary>> GetForSiteAsync(
        SiteId siteId, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EnabledModuleSummary>>(
            _bySite.TryGetValue(siteId, out var list) ? [.. list] : []);
}
