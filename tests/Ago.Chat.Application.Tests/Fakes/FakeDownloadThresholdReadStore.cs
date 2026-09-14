using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`25-83`: <see cref="IDownloadThresholdReadStore"/> in memory - a missing tier resolves to
/// <see cref="DownloadThresholds.Unbounded"/>, the identical "fails open" contract the real Dapper
/// store honours for a tier with no configured row.</summary>
public sealed class FakeDownloadThresholdReadStore : IDownloadThresholdReadStore
{
    private readonly Dictionary<string, DownloadThresholds> _byTier = [];

    public void Seed(string tier, long softThresholdBytes, long hardThresholdBytes) =>
        _byTier[tier] = new DownloadThresholds(tier, softThresholdBytes, hardThresholdBytes);

    public Task<DownloadThresholds> GetForTierAsync(string tier, CancellationToken cancellationToken) =>
        Task.FromResult(_byTier.GetValueOrDefault(tier) ?? DownloadThresholds.Unbounded(tier));
}
