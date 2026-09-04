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

    // `23-14`: interface parity only - no Application-layer handler test exercises the owner's
    // detail read through this fake today (it is proven against real Postgres in
    // Ago.Chat.Integration.Tests, the same split GetForSiteAsync's own remarks describe). Every
    // seeded summary here was seeded with no expiry, so IsActive is always true and nothing seeded
    // is ever excluded - the identical "no expiry filtering to fake" reasoning GetForSiteAsync's own
    // remarks already give, restated for the unfiltered method.
    public Task<IReadOnlyList<EnabledModuleDetailSummary>> GetAllForSiteAsync(
        SiteId siteId, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EnabledModuleDetailSummary>>(
            _bySite.TryGetValue(siteId, out var list)
                ? [.. list.Select(s => new EnabledModuleDetailSummary(
                    s.ModuleKey, s.TriggerWords, s.EntryPoint, s.GrantedByOwner, s.ExpiresAt, IsActive: true))]
                : []);
}
