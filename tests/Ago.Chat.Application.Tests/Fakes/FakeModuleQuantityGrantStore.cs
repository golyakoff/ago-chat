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

    public Task<int> GetQuantityAsync(SiteId siteId, ModuleKey moduleKey, CancellationToken cancellationToken) =>
        Task.FromResult(EffectiveQuantity(siteId, moduleKey));

    public Task<IReadOnlyDictionary<ModuleKey, int>> GetAllForSiteAsync(
        SiteId siteId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<ModuleKey, int>>(
            Grants.Where(kv => kv.Key.Item1 == siteId)
                .Select(kv => kv.Key.Item2)
                .Distinct()
                .ToDictionary(moduleKey => moduleKey, moduleKey => EffectiveQuantity(siteId, moduleKey)));

    public Task GrantAsync(
        SiteId siteId, ModuleKey moduleKey, int quantity, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Grants[(siteId, moduleKey)] = quantity;
        return Task.CompletedTask;
    }

    public Task SetUnconditionalGrantAsync(
        SiteId siteId, ModuleKey moduleKey, bool unconditionallyGranted, string setBy, string reason,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        UnconditionalGrants[(siteId, moduleKey)] = unconditionallyGranted;
        return Task.CompletedTask;
    }

    private int EffectiveQuantity(SiteId siteId, ModuleKey moduleKey)
    {
        var quantity = Grants.GetValueOrDefault((siteId, moduleKey));
        return UnconditionalGrants.GetValueOrDefault((siteId, moduleKey)) ? Math.Max(quantity, 1) : quantity;
    }
}
