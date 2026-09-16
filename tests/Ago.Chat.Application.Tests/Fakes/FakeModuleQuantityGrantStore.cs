using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Records every call, so a test can assert exactly what was (or was not) granted - the same
/// hand-written-fake reasoning every other fake in this suite follows (testing.md).
///
/// <para>`23-85`: promoted here from its original home next to <c>GrantModuleQuantityHandlerTests</c>
/// (`GrantModuleQuantityAsOwnerHandlerTests` already reached across into that file's own namespace to
/// reuse it) - this item adds a third and fourth consumer
/// (<c>ListNonEntitledChannelCredentialsAsOwnerHandlerTests</c>,
/// <c>DisconnectNonEntitledChannelCredentialsAsOwnerHandlerTests</c>) beside the three
/// already-existing ones that check a channel entitlement, at which point "reach into another test
/// class's own file" stops being the shorter path and the shared <c>Fakes/</c> folder every other
/// cross-suite fake already lives in is.</para></summary>
public sealed class FakeModuleQuantityGrantStore : IModuleQuantityGrantStore
{
    public Dictionary<(SiteId, ModuleKey), int> Grants { get; } = [];

    /// <summary>`23-86`: the platform owner's own unconditional-grant flag, tracked separately from
    /// <see cref="Grants"/> - the identical "a second, independent input, never a second write to the
    /// same field" shape <see cref="Domain.ModuleQuantityGrant"/>'s own remarks state for the real
    /// store.</summary>
    public Dictionary<(SiteId, ModuleKey), bool> UnconditionalGrants { get; } = [];

    /// <summary>`25-115`: tracked separately from <see cref="UnconditionalGrants"/> for the identical
    /// reason that dictionary is tracked separately from <see cref="Grants"/> - a third, independent
    /// input a test can set without disturbing the other two. Absent key or a <see langword="null"/>
    /// value both mean "no expiry" (indefinite) - <see cref="EffectiveQuantity"/> reads either the same
    /// way.</summary>
    public Dictionary<(SiteId, ModuleKey), DateTimeOffset?> UnconditionalGrantExpiresAt { get; } = [];

    public Task<int> GetQuantityAsync(SiteId siteId, ModuleKey moduleKey, CancellationToken cancellationToken) =>
        Task.FromResult(EffectiveQuantity(siteId, moduleKey));

    public Task<IReadOnlyDictionary<ModuleKey, int>> GetAllForSiteAsync(
        SiteId siteId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<ModuleKey, int>>(
            Grants.Where(kv => kv.Key.Item1 == siteId)
                .Select(kv => kv.Key.Item2)
                .Distinct()
                .ToDictionary(moduleKey => moduleKey, moduleKey => EffectiveQuantity(siteId, moduleKey)));

    /// <summary>`25-115`: rebuilds a real <see cref="ModuleQuantityGrant"/> aggregate from this fake's
    /// own three dictionaries for every (site, module) key either <see cref="Grants"/> or
    /// <see cref="UnconditionalGrants"/> knows about - a caller that needs the aggregate's own state
    /// (not just the OR'd int <see cref="GetQuantityAsync"/> gives) gets the identical shape the real
    /// store would hand back, built through the aggregate's own constructors rather than a second,
    /// parallel DTO this fake would have to keep in sync by hand.</summary>
    public Task<IReadOnlyList<ModuleQuantityGrant>> GetGrantsForSiteAsync(
        SiteId siteId, CancellationToken cancellationToken)
    {
        var keys = Grants.Keys.Concat(UnconditionalGrants.Keys)
            .Where(k => k.Item1 == siteId)
            .Select(k => k.Item2)
            .Distinct();

        var grants = keys.Select(moduleKey =>
        {
            var grant = ModuleQuantityGrant.Grant(siteId, moduleKey, Grants.GetValueOrDefault((siteId, moduleKey)), FixedAuditTime);
            if (UnconditionalGrants.TryGetValue((siteId, moduleKey), out var unconditionallyGranted))
            {
                grant.SetUnconditionalGrant(
                    unconditionallyGranted, "fake-owner-sub", "fake reason", FixedAuditTime,
                    UnconditionalGrantExpiresAt.GetValueOrDefault((siteId, moduleKey)));
            }

            return grant;
        }).ToList();

        return Task.FromResult<IReadOnlyList<ModuleQuantityGrant>>(grants);
    }

    public Task GrantAsync(
        SiteId siteId, ModuleKey moduleKey, int quantity, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Grants[(siteId, moduleKey)] = quantity;
        return Task.CompletedTask;
    }

    public Task SetUnconditionalGrantAsync(
        SiteId siteId, ModuleKey moduleKey, bool unconditionallyGranted, string setBy, string reason,
        DateTimeOffset now, CancellationToken cancellationToken, DateTimeOffset? expiresAt = null)
    {
        UnconditionalGrants[(siteId, moduleKey)] = unconditionallyGranted;
        UnconditionalGrantExpiresAt[(siteId, moduleKey)] = expiresAt;
        return Task.CompletedTask;
    }

    // `25-115`: GetGrantsForSiteAsync's own audit timestamp, deliberately not `now` - this fake never
    // received a clock (it is called from handlers that already carry their own FakeClock), and no
    // test in this suite reads GrantedAt/UnconditionalGrantSetAt off the reconstructed aggregate, only
    // EffectiveQuantity(now) and the raw UnconditionallyGrantedByOwner/UnconditionalGrantExpiresAt
    // fields.
    private static readonly DateTimeOffset FixedAuditTime = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private int EffectiveQuantity(SiteId siteId, ModuleKey moduleKey)
    {
        var quantity = Grants.GetValueOrDefault((siteId, moduleKey));
        var unconditionallyGranted = UnconditionalGrants.GetValueOrDefault((siteId, moduleKey));
        var expiresAt = UnconditionalGrantExpiresAt.GetValueOrDefault((siteId, moduleKey));
        var stillLive = unconditionallyGranted && (expiresAt is not { } expiry || expiry > DateTimeOffset.UtcNow);
        return stillLive ? Math.Max(quantity, 1) : quantity;
    }
}
