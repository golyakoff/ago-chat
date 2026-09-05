using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`23-17`: the same "good enough to test the handler's own merging logic without a real
/// Postgres query" shape <see cref="FakeOperatorAnalyticsReadStore"/> already establishes for its own
/// sibling port. Defaults to an empty report - the same "no data" case
/// <see cref="Ago.Chat.Contracts.OperatorLoadSummaryDto"/>'s own remarks describe, so a test that never
/// seeds this fake exercises the handler exactly as it behaved before this item, with every merged row's
/// own <c>Load</c> left <see langword="null"/>.</summary>
public sealed class FakeOperatorLoadReportReadStore : IOperatorLoadReportReadStore
{
    private IReadOnlyList<OperatorLoadSummary> _result = [];

    public List<(SiteId SiteId, DateTimeOffset From, DateTimeOffset To)> Calls { get; } = [];

    public void Seed(IReadOnlyList<OperatorLoadSummary> result) => _result = result;

    public Task<IReadOnlyList<OperatorLoadSummary>> GetOperatorLoadReportAsync(
        SiteId siteId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        Calls.Add((siteId, from, to));
        return Task.FromResult(_result);
    }
}
