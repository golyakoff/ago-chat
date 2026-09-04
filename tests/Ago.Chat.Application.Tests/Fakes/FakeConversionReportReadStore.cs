using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Records the exact bound `GetConversionReportForSiteHandler` resolved and hands back
/// whatever this fake was seeded with - the same shape `FakeOperatorAnalyticsReadStore` already
/// establishes for `18-08`'s analogous handler.
///
/// <para><b>`23-16`: <see cref="Calls"/> records every invocation, in call order</b> - the handler now
/// calls this port twice per request (current window, then preceding window,
/// `PrecedingPeriod`'s own remarks), and a test proving the handler never lets the second call's window
/// address a different site, or swap the two windows, needs to see both calls, not just the most recent
/// one. <see cref="LastSiteId"/>/<see cref="LastFrom"/>/<see cref="LastTo"/> stay - they are
/// <see cref="Calls"/>'s own last entry, kept for the single-call tests that predate this.
/// <see cref="SeedSequence"/> lets a test hand back a different result to the first call than the
/// second, so the handler's own current/previous mapping is provable end to end without a real
/// Postgres query.</para>
/// </summary>
public sealed class FakeConversionReportReadStore : IConversionReportReadStore
{
    private static readonly ConversionReportResult DefaultResult = new(new ConversionBucket(0, 0, 0, 0, 0, null), []);

    private ConversionReportResult _result = DefaultResult;
    private IReadOnlyList<ConversionReportResult>? _sequence;

    public List<(SiteId SiteId, DateTimeOffset From, DateTimeOffset To)> Calls { get; } = [];

    public SiteId? LastSiteId => Calls.Count == 0 ? null : Calls[^1].SiteId;

    public DateTimeOffset? LastFrom => Calls.Count == 0 ? null : Calls[^1].From;

    public DateTimeOffset? LastTo => Calls.Count == 0 ? null : Calls[^1].To;

    public void Seed(ConversionReportResult result) => _result = result;

    /// <summary>The first call gets <paramref name="results"/>[0], the second <paramref name="results"/>[1],
    /// and so on; a call past the end of the array repeats the last element, so a test only needs to name
    /// as many results as it actually cares about distinguishing.</summary>
    public void SeedSequence(params ConversionReportResult[] results) => _sequence = results;

    public Task<ConversionReportResult> GetConversionReportAsync(
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
