using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>
/// `25-170`: a permissive stand-in for the pair `ChannelEntitlement.IsEntitledAsync` composes
/// (<see cref="IBillingOptionEntitlementProvider"/>/<see cref="IModuleQuantityGrantStore"/>), for the
/// many existing tests across this suite that are about something other than channel entitlement (a
/// visitor/conversation/identity resolution) and would otherwise all need to seed a real mapping and a
/// real grant, per site, just to keep compiling and passing. <see cref="FakeBillingOptionEntitlementProvider"/>/
/// <see cref="FakeModuleQuantityGrantStore"/> stay the real fakes for tests that are actually about
/// entitlement (see <c>ReceiveChannelMessageHandlerTests</c>/`ReceiveChannelAttachmentHandlerTests`'s own
/// entitlement-specific cases).
/// </summary>
public sealed class AlwaysEntitledBillingOptionEntitlementProvider : IBillingOptionEntitlementProvider
{
    private static readonly ModuleKey DummyModuleKey = new("test-always-entitled-channel");

    public ModuleKey? TryGet(BillingOptionKey optionKey) => DummyModuleKey;
}

/// <summary>See <see cref="AlwaysEntitledBillingOptionEntitlementProvider"/>'s own remarks - every
/// (site, module) reads as quantity 1, regardless of what was ever granted.</summary>
public sealed class AlwaysEntitledModuleQuantityGrantStore : IModuleQuantityGrantStore
{
    public Task<int> GetQuantityAsync(SiteId siteId, ModuleKey moduleKey, CancellationToken cancellationToken) =>
        Task.FromResult(1);

    public Task<IReadOnlyDictionary<ModuleKey, int>> GetAllForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<ModuleKey, int>>(new Dictionary<ModuleKey, int>());

    public Task<IReadOnlyList<ModuleQuantityGrant>> GetGrantsForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ModuleQuantityGrant>>([]);

    public Task GrantAsync(SiteId siteId, ModuleKey moduleKey, int quantity, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task SetUnconditionalGrantAsync(
        SiteId siteId, ModuleKey moduleKey, bool unconditionallyGranted, string setBy, string reason,
        DateTimeOffset now, CancellationToken cancellationToken, DateTimeOffset? expiresAt = null) =>
        Task.CompletedTask;
}
