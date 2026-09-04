using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Records the exact bound `GetModuleFlowReportForSiteHandler` resolved and hands back
/// whatever this fake was seeded with - the same "good enough to test the handler's own access-check
/// and bound-defaulting logic without a real Postgres query" shape
/// <see cref="FakeOperatorAnalyticsReadStore"/> already establishes for `18-08`'s analogous
/// handler.
///
/// <para><b>`23-16`: <see cref="Calls"/> records every invocation, in call order</b> - see
/// <see cref="FakeConversionReportReadStore"/>'s own remarks for why.</para>
/// </summary>
public sealed class FakeModuleFlowReadStore : IModuleFlowReadStore
{
    private static readonly ModuleFlowReportResult DefaultResult = new(0, 0);

    private ModuleFlowReportResult _result = DefaultResult;
    private IReadOnlyList<ModuleFlowReportResult>? _sequence;

    public List<(SiteId SiteId, ModuleKey ModuleKey, DateTimeOffset From, DateTimeOffset To)> Calls { get; } = [];

    public SiteId? LastSiteId => Calls.Count == 0 ? null : Calls[^1].SiteId;

    public ModuleKey? LastModuleKey => Calls.Count == 0 ? null : Calls[^1].ModuleKey;

    public DateTimeOffset? LastFrom => Calls.Count == 0 ? null : Calls[^1].From;

    public DateTimeOffset? LastTo => Calls.Count == 0 ? null : Calls[^1].To;

    public void Seed(ModuleFlowReportResult result) => _result = result;

    /// <summary>The first call gets <paramref name="results"/>[0], the second <paramref name="results"/>[1],
    /// and so on; a call past the end of the array repeats the last element.</summary>
    public void SeedSequence(params ModuleFlowReportResult[] results) => _sequence = results;

    public Task<ModuleFlowReportResult> GetSiteModuleFlowReportAsync(
        SiteId siteId, ModuleKey moduleKey, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var callIndex = Calls.Count;
        Calls.Add((siteId, moduleKey, from, to));

        if (_sequence is { Count: > 0 } sequence)
        {
            return Task.FromResult(sequence[Math.Min(callIndex, sequence.Count - 1)]);
        }

        return Task.FromResult(_result);
    }
}
