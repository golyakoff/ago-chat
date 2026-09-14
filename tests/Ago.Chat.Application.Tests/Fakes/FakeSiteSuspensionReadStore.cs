using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`22-08`: a plain in-memory stand-in for <see cref="ISiteSuspensionReadStore"/> - a test
/// sets a site's own <see cref="Domain.Site.SuspendedUntil"/> directly via <see cref="Suspend"/>, the
/// same "settable in a test, never ambient" shape <see cref="FakeClock"/>'s own remarks describe for
/// itself.</summary>
public sealed class FakeSiteSuspensionReadStore : ISiteSuspensionReadStore
{
    private readonly Dictionary<SiteId, DateTimeOffset> _suspendedUntil = [];

    // `25-70`: when a site suspended through the plain `Suspend(siteId, until)` overload every other
    // test already calls, this fake has no real "since" to report - real suspend/extend go through
    // separate `site_suspensions` rows the production adapter reads, which a two-field dictionary
    // cannot model faithfully. Tests that care about `GetForTenantAsync`'s own `Since` value call
    // `Suspend(siteId, until, since)` instead.
    private readonly Dictionary<SiteId, DateTimeOffset> _suspendedSince = [];

    public void Suspend(SiteId siteId, DateTimeOffset until) => _suspendedUntil[siteId] = until;

    public void Suspend(SiteId siteId, DateTimeOffset until, DateTimeOffset since)
    {
        _suspendedUntil[siteId] = until;
        _suspendedSince[siteId] = since;
    }

    public Task<bool> IsSuspendedAsync(SiteId siteId, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult(_suspendedUntil.TryGetValue(siteId, out var until) && until > now);

    public Task<IReadOnlyList<SiteId>> ListActiveSuspensionsAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SiteId>>(
            _suspendedUntil.Where(kv => kv.Value > now).Select(kv => kv.Key).ToList());

    public Task<IReadOnlyList<OwnerSuspensionSummary>> ListForOwnerAsync(
        DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OwnerSuspensionSummary>>(
            _suspendedUntil.Where(kv => kv.Value > now)
                .Select(kv => new OwnerSuspensionSummary(kv.Key, "site", kv.Value, "owner", "reason", now))
                .ToList());

    public Task<TenantSuspensionStatus> GetForTenantAsync(
        SiteId siteId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!_suspendedUntil.TryGetValue(siteId, out var until) || until <= now)
        {
            return Task.FromResult(new TenantSuspensionStatus(IsSuspended: false, Since: null, Until: null));
        }

        var since = _suspendedSince.TryGetValue(siteId, out var recordedSince) ? recordedSince : (DateTimeOffset?)null;
        return Task.FromResult(new TenantSuspensionStatus(IsSuspended: true, since, until));
    }
}
