using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`25-83`: <see cref="IDownloadThresholdReadStore"/> in memory - a missing tier resolves to
/// <see cref="DownloadThresholds.Unbounded"/>, the identical "fails open" contract the real Dapper
/// store honours for a tier with no configured row.</summary>
public sealed class FakeDownloadThresholdReadStore : IDownloadThresholdReadStore
{
    private readonly Dictionary<string, DownloadThresholds> _byTier = [];

    /// <summary>`25-84`: <paramref name="autoBillCapRub"/> defaults to <see langword="null"/> - uncapped -
    /// so every pre-existing call site keeps meaning exactly what it meant before this item, and only a
    /// test that is actually about the cap has to mention it.</summary>
    public void Seed(string tier, long softThresholdBytes, long hardThresholdBytes, decimal? autoBillCapRub = null) =>
        _byTier[tier] = new DownloadThresholds(tier, softThresholdBytes, hardThresholdBytes, autoBillCapRub);

    public Task<DownloadThresholds> GetForTierAsync(string tier, CancellationToken cancellationToken) =>
        Task.FromResult(_byTier.GetValueOrDefault(tier) ?? DownloadThresholds.Unbounded(tier));
}
