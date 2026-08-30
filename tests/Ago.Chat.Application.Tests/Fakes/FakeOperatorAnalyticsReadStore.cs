using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Records the exact bound `GetOperatorAnalyticsForSiteHandler` resolved and hands back
/// whatever this fake was seeded with - the same "good enough to test the handler's own access-check
/// and bound-defaulting logic without a real Postgres query" shape
/// <see cref="FakeConversationSearchStore"/> already establishes for `18-01`'s analogous
/// handler.</summary>
public sealed class FakeOperatorAnalyticsReadStore : IOperatorAnalyticsReadStore
{
    private OperatorAnalyticsResult _result = new(new OperatorAnalyticsBucket(0, null, null, 0), [], [], [], []);

    public SiteId? LastSiteId { get; private set; }

    public DateTimeOffset? LastFrom { get; private set; }

    public DateTimeOffset? LastTo { get; private set; }

    public void Seed(OperatorAnalyticsResult result) => _result = result;

    public Task<OperatorAnalyticsResult> GetSiteAnalyticsAsync(
        SiteId siteId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        LastSiteId = siteId;
        LastFrom = from;
        LastTo = to;

        return Task.FromResult(_result);
    }
}
