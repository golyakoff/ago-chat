using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Mirrors the real repository's own once-a-minute throttle on both writes, and its
/// <see cref="SiteInstallationSignals.None"/> default for a site nothing has ever written a signal
/// for - good enough to test a handler's own reading and folding of these facts without a real
/// Postgres.</summary>
public sealed class FakeSiteInstallationSignalRepository : ISiteInstallationSignalRepository
{
    private static readonly TimeSpan ThrottleWindow = TimeSpan.FromMinutes(1);

    private readonly Dictionary<SiteId, SiteInstallationSignals> _bySite = [];

    /// <summary>How many times the row write actually happened (as opposed to being throttled away) -
    /// what `23-06`'s own Done-when asks a test to prove: "two mints inside one minute and one row
    /// write."</summary>
    public int SightingWriteCount { get; private set; }

    public int RefusalWriteCount { get; private set; }

    public void Seed(SiteId siteId, SiteInstallationSignals signals) => _bySite[siteId] = signals;

    public Task RecordSightingAsync(SiteId siteId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var current = _bySite.TryGetValue(siteId, out var existing) ? existing : SiteInstallationSignals.None;
        if (current.LastSeenAt is { } lastSeenAt && now - lastSeenAt < ThrottleWindow)
        {
            return Task.CompletedTask;
        }

        _bySite[siteId] = current with { FirstSeenAt = current.FirstSeenAt ?? now, LastSeenAt = now };
        SightingWriteCount++;
        return Task.CompletedTask;
    }

    public Task RecordRefusedOriginAsync(SiteId siteId, string origin, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var current = _bySite.TryGetValue(siteId, out var existing) ? existing : SiteInstallationSignals.None;
        if (current.LastRefusedOriginAt is { } lastRefusedAt && now - lastRefusedAt < ThrottleWindow)
        {
            return Task.CompletedTask;
        }

        _bySite[siteId] = current with { LastRefusedOrigin = origin, LastRefusedOriginAt = now };
        RefusalWriteCount++;
        return Task.CompletedTask;
    }

    public Task<SiteInstallationSignals> GetAsync(SiteId siteId, CancellationToken cancellationToken) =>
        Task.FromResult(_bySite.TryGetValue(siteId, out var existing) ? existing : SiteInstallationSignals.None);
}
