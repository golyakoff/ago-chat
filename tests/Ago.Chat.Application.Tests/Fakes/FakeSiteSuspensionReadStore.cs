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

    public void Suspend(SiteId siteId, DateTimeOffset until) => _suspendedUntil[siteId] = until;

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
}
