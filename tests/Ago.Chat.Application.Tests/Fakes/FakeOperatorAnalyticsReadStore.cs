using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Records the exact bound `GetOperatorAnalyticsForSiteHandler` resolved and hands back
/// whatever this fake was seeded with - the same "good enough to test the handler's own access-check
/// and bound-defaulting logic without a real Postgres query" shape
/// <see cref="FakeConversationSearchStore"/> already establishes for `18-01`'s analogous
/// handler.
///
/// <para><b>`23-16`: <see cref="Calls"/> records every invocation, in call order</b> - see
/// <see cref="FakeConversionReportReadStore"/>'s own remarks for why (the handler now calls this port
/// twice per request, current window then preceding window). <see cref="LastSiteId"/>/
/// <see cref="LastFrom"/>/<see cref="LastTo"/> stay, reading <see cref="Calls"/>'s own last entry, for
/// the single-call tests that predate this.</para>
/// </summary>
public sealed class FakeOperatorAnalyticsReadStore : IOperatorAnalyticsReadStore
{
    private static readonly OperatorAnalyticsResult DefaultResult =
        new(new OperatorAnalyticsBucket(0, null, null, 0), [], [], [], []);

    private OperatorAnalyticsResult _result = DefaultResult;
    private IReadOnlyList<OperatorAnalyticsResult>? _sequence;

    public List<(SiteId SiteId, DateTimeOffset From, DateTimeOffset To)> Calls { get; } = [];

    public SiteId? LastSiteId => Calls.Count == 0 ? null : Calls[^1].SiteId;

    public DateTimeOffset? LastFrom => Calls.Count == 0 ? null : Calls[^1].From;

    public DateTimeOffset? LastTo => Calls.Count == 0 ? null : Calls[^1].To;

    public void Seed(OperatorAnalyticsResult result) => _result = result;

    /// <summary>The first call gets <paramref name="results"/>[0], the second <paramref name="results"/>[1],
    /// and so on; a call past the end of the array repeats the last element.</summary>
    public void SeedSequence(params OperatorAnalyticsResult[] results) => _sequence = results;

    public Task<OperatorAnalyticsResult> GetSiteAnalyticsAsync(
        SiteId siteId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var callIndex = Calls.Count;
        Calls.Add((siteId, from, to));

        if (_sequence is { Count: > 0 } sequence)
        {
            return Task.FromResult(sequence[Math.Min(callIndex, sequence.Count - 1)]);
        }

        return Task.FromResult(_result);
    }
}
