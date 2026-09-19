using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-170`: a permissive stand-in for the pair `ChannelEntitlement.IsEntitledAsync` composes
/// (<see cref="IBillingOptionEntitlementProvider"/>/<see cref="IModuleQuantityGrantStore"/>), for the
/// many existing integration tests in this project that construct
/// <c>ReceiveChannelMessageHandler</c>/<c>ReceiveChannelAttachmentHandler</c> directly to test something
/// other than channel entitlement (a real Postgres round trip through identity/visitor/conversation
/// resolution) and would otherwise each need a real `channel_credentials`-shaped entitlement seeded
/// against a real site just to keep passing. Mirrors
/// <c>Ago.Chat.Application.Tests.Fakes.AlwaysEntitledBillingOptionEntitlementProvider</c> exactly - that
/// project is not referenced from here, so this is a deliberate, small restatement rather than a shared
/// dependency neither test project otherwise needs.
/// </summary>
public sealed class AlwaysEntitledBillingOptionEntitlementProvider : IBillingOptionEntitlementProvider
{
    private static readonly ModuleKey DummyModuleKey = new("test-always-entitled-channel");

    public ModuleKey? TryGet(BillingOptionKey optionKey) => DummyModuleKey;
}

/// <summary>See <see cref="AlwaysEntitledBillingOptionEntitlementProvider"/>'s own remarks - every
/// (site, module) reads as quantity 1, regardless of what was ever granted in the real database.</summary>
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
