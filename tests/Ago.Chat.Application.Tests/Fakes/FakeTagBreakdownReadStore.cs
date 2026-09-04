using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Records the exact bound `GetTagBreakdownReportForSiteHandler` resolved and hands back
/// whatever this fake was seeded with - the same shape `FakeConversionReportReadStore` already
/// establishes for `18-10`'s analogous handler.
///
/// <para><b>`23-16`: <see cref="Calls"/> records every invocation, in call order</b> - see
/// <see cref="FakeConversionReportReadStore"/>'s own remarks for why.</para>
/// </summary>
public sealed class FakeTagBreakdownReadStore : ITagBreakdownReadStore
{
    private static readonly TagBreakdownResult DefaultResult = new(0, 0, null, []);

    private TagBreakdownResult _result = DefaultResult;
    private IReadOnlyList<TagBreakdownResult>? _sequence;

    public List<(SiteId SiteId, DateTimeOffset From, DateTimeOffset To)> Calls { get; } = [];

    public SiteId? LastSiteId => Calls.Count == 0 ? null : Calls[^1].SiteId;

    public DateTimeOffset? LastFrom => Calls.Count == 0 ? null : Calls[^1].From;

    public DateTimeOffset? LastTo => Calls.Count == 0 ? null : Calls[^1].To;

    public void Seed(TagBreakdownResult result) => _result = result;

    /// <summary>The first call gets <paramref name="results"/>[0], the second <paramref name="results"/>[1],
    /// and so on; a call past the end of the array repeats the last element.</summary>
    public void SeedSequence(params TagBreakdownResult[] results) => _sequence = results;

    public Task<TagBreakdownResult> GetTagBreakdownAsync(
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
