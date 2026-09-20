using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`25-181`: records every grant, so a test can assert exactly what was granted - the same
/// hand-written-fake reasoning every other fake in this suite follows (testing.md). Computes the live
/// extra through a real <see cref="OwnerSeatGrant"/> aggregate rather than a second, parallel
/// int-tracking dictionary, so <see cref="OwnerSeatGrant.EffectiveQuantity"/>'s own expiry rule is
/// exercised exactly as the real store exercises it.</summary>
public sealed class FakeOwnerSeatGrantStore : IOwnerSeatGrantStore
{
    private readonly Dictionary<(SiteId SiteId, OwnerSeatGrantRole Role), OwnerSeatGrant> _grants = [];

    public Task<int> GetEffectiveExtraAsync(
        SiteId siteId, OwnerSeatGrantRole role, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult(_grants.TryGetValue((siteId, role), out var grant) ? grant.EffectiveQuantity(now) : 0);

    public Task GrantAsync(
        SiteId siteId, OwnerSeatGrantRole role, int quantity, string grantedBy, string reason, DateTimeOffset now,
        DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        if (_grants.TryGetValue((siteId, role), out var grant))
        {
            grant.SetGrant(quantity, grantedBy, reason, now, expiresAt);
        }
        else
        {
            _grants[(siteId, role)] = OwnerSeatGrant.Grant(siteId, role, quantity, grantedBy, reason, now, expiresAt);
        }

        return Task.CompletedTask;
    }

    /// <summary>Lets a test assert exactly what was granted, without re-deriving it from
    /// <see cref="GetEffectiveExtraAsync"/>.</summary>
    public OwnerSeatGrant? Get(SiteId siteId, OwnerSeatGrantRole role) => _grants.GetValueOrDefault((siteId, role));
}
