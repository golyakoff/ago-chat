using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.DisableModuleForSite;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.DisableModuleForSite;

/// <summary>
/// `26-316`'s own Done-when for the off half of the toggle: a tenant admin can turn their own module off,
/// non-destructively (the row is tombstoned, not deleted, and the module-side registration is revoked, not
/// erased), the call refuses without the permission and for a site the caller does not administer, and a
/// platform-owner grant is left untouched (the override stays an override).
/// </summary>
public class DisableModuleForSiteHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId Admin = new(Guid.NewGuid());

    private sealed record Fixture(
        DisableModuleForSiteHandler Handler, FakePermissionChecker Permissions, FakeEnabledModuleRepository Modules,
        FakeModuleRegistrationGateway RegistrationGateway, FakeModuleProvisioningSecretProvider ProvisioningSecrets);

    private static Fixture CreateFixture(bool granted = true, bool grantedByOwner = false, bool seedRow = true)
    {
        var permissions = new FakePermissionChecker();
        if (granted)
        {
            permissions.Grant(Admin, SiteId, Permission.SiteConfigure);
        }

        var modules = new FakeEnabledModuleRepository();
        if (seedRow)
        {
            modules.SaveAsync(
                new EnabledModule(
                    new EnabledModuleId(Guid.NewGuid()), SiteId, new ModuleKey("calendar"), ["/записаться"],
                    new Uri("https://calendar.example.com"), new ModuleCredential("an-existing-secret-of-sixteen-plus"),
                    Now.AddDays(-1), grantedByOwner: grantedByOwner),
                CancellationToken.None).GetAwaiter().GetResult();
        }

        var registrationGateway = new FakeModuleRegistrationGateway();
        var provisioningSecrets = new FakeModuleProvisioningSecretProvider();

        var handler = new DisableModuleForSiteHandler(
            permissions, modules, registrationGateway, provisioningSecrets, new FakeClock(Now));
        return new Fixture(handler, permissions, modules, registrationGateway, provisioningSecrets);
    }

    private static Application.UseCases.DisableModuleForSite.DisableModuleForSite Command(SiteId? siteId = null) =>
        new(Admin, siteId ?? SiteId, "calendar");

    [Fact]
    public async Task HandleAsync_WithPermission_DisablesTheModule_NonDestructively_AndRevokesTheRegistration()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        // Non-destructive: the row survives, tombstoned rather than deleted (`22-30`), so a re-enable and
        // the erasure job can both still find the module's own history.
        var saved = Assert.Single(fixture.Modules.All);
        Assert.Equal(Now, saved.RevokedAt);
        // Deactivated, not erased: RevokeAsync (registration off), never EraseTenantDataAsync (data gone).
        Assert.Single(fixture.RegistrationGateway.RevokeCalls);
        Assert.Empty(fixture.RegistrationGateway.EraseTenantDataCalls);
        // And it is no longer the current registration for this site.
        Assert.Null(await fixture.Modules.GetAsync(SiteId, new ModuleKey("calendar"), CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_WithoutThePermission_ReturnsForbidden_AndDisablesNothing()
    {
        var fixture = CreateFixture(granted: false);

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Null(Assert.Single(fixture.Modules.All).RevokedAt);
        Assert.Empty(fixture.RegistrationGateway.RevokeCalls);
    }

    [Fact]
    public async Task HandleAsync_ForASiteTheCallerDoesNotAdminister_ReturnsForbidden()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(new SiteId(Guid.NewGuid())), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(fixture.RegistrationGateway.RevokeCalls);
    }

    [Fact]
    public async Task HandleAsync_WhenTheModuleIsNotEnabled_ReturnsModuleNotEnabled()
    {
        var fixture = CreateFixture(seedRow: false);

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.NotEnabled", result.Error!.Value.Code);
        Assert.Empty(fixture.RegistrationGateway.RevokeCalls);
    }

    /// <summary>The override stays an override: a tenant cannot turn off a module a platform owner granted.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheGrantWasMadeByThePlatformOwner_RefusesAndLeavesItEnabled()
    {
        var fixture = CreateFixture(grantedByOwner: true);

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.DisableOwnerGrantRefused", result.Error!.Value.Code);
        Assert.Null(Assert.Single(fixture.Modules.All).RevokedAt);
        Assert.Empty(fixture.RegistrationGateway.RevokeCalls);
    }

    /// <summary>Module-first ordering: if the module-side revoke fails, Chat's own row is left enabled.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheModuleRefusesTheRevoke_LeavesItEnabled()
    {
        var fixture = CreateFixture();
        fixture.RegistrationGateway.UnreachableOnRevoke = true;

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.RegistrationFailed", result.Error!.Value.Code);
        Assert.Null(Assert.Single(fixture.Modules.All).RevokedAt);
    }

    [Fact]
    public async Task HandleAsync_WhenNoProvisioningSecretIsConfigured_ReturnsProvisioningNotConfigured()
    {
        var fixture = CreateFixture();
        fixture.ProvisioningSecrets.Secret = null;

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.ProvisioningNotConfigured", result.Error!.Value.Code);
        Assert.Empty(fixture.RegistrationGateway.RevokeCalls);
    }
}
