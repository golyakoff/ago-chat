using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Records the exact bound `GetModuleFlowReportForSiteHandler` resolved and hands back
/// whatever this fake was seeded with - the same "good enough to test the handler's own access-check
/// and bound-defaulting logic without a real Postgres query" shape
/// <see cref="FakeOperatorAnalyticsReadStore"/> already establishes for `18-08`'s analogous
/// handler.</summary>
public sealed class FakeModuleFlowReadStore : IModuleFlowReadStore
{
    private ModuleFlowReportResult _result = new(0, 0);

    public SiteId? LastSiteId { get; private set; }

    public ModuleKey? LastModuleKey { get; private set; }

    public DateTimeOffset? LastFrom { get; private set; }

    public DateTimeOffset? LastTo { get; private set; }

    public void Seed(ModuleFlowReportResult result) => _result = result;

    public Task<ModuleFlowReportResult> GetSiteModuleFlowReportAsync(
        SiteId siteId, ModuleKey moduleKey, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        LastSiteId = siteId;
        LastModuleKey = moduleKey;
        LastFrom = from;
        LastTo = to;

        return Task.FromResult(_result);
    }
}
