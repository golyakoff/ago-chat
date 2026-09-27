using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`26-223`: hands back whatever freshness instant it was seeded with (default
/// <see langword="null"/>, the "pipeline not configured / no run yet" case) and records that it was
/// called - enough to prove the handler surfaces <c>computedAsOf</c> on the response without a real
/// <c>ago_analytics</c> query, the same shape <see cref="FakeOperatorAnalyticsReadStore"/> already uses.</summary>
public sealed class FakeAnalyticsFreshnessReadStore : IAnalyticsFreshnessReadStore
{
    private DateTimeOffset? _lastCompletedAt;

    public int CallCount { get; private set; }

    public void Seed(DateTimeOffset? lastCompletedAt) => _lastCompletedAt = lastCompletedAt;

    public Task<DateTimeOffset?> GetLastRollupCompletedAtAsync(CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(_lastCompletedAt);
    }
}
