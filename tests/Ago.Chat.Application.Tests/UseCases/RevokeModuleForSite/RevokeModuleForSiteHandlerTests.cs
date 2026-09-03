using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.RevokeModuleForSite;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RevokeModuleForSite;

/// <summary>`22-11`'s own third Done-when, at the Application level.</summary>
public class RevokeModuleForSiteHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly ModuleKey Calendar = new("calendar");
    private static readonly Uri EntryPoint = new("https://calendar.example.com");
    private const string ValidProvisioningSecret = "a-provisioning-secret-of-sixteen-plus-chars";

    private sealed record Fixture(
        RevokeModuleForSiteHandler Handler, FakeEnabledModuleRepository Modules, FakePermissionChecker Permissions,
        FakeModuleRegistrationGateway RegistrationGateway, EnabledModuleId ExistingId);

    private static async Task<Fixture> CreateFixtureAsync(bool permitted = true, bool seeded = true)
    {
        var modules = new FakeEnabledModuleRepository();
        var permissions = new FakePermissionChecker();
        var registrationGateway = new FakeModuleRegistrationGateway();
        if (permitted)
        {
            permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        }

        var existingId = new EnabledModuleId(Guid.NewGuid());
        if (seeded)
        {
            var existing = new EnabledModule(
                existingId, SiteId, Calendar, ["/booking"], EntryPoint,
                new ModuleCredential("a-shared-secret-of-sixteen-plus-chars"), Now);
            await modules.SaveAsync(existing, CancellationToken.None);
        }

        var handler = new RevokeModuleForSiteHandler(modules, permissions, registrationGateway);
        return new Fixture(handler, modules, permissions, registrationGateway, existingId);
    }

    private static Application.UseCases.RevokeModuleForSite.RevokeModuleForSite Command() =>
        new(OperatorId, SiteId, Calendar.Value, ValidProvisioningSecret);

    [Fact]
    public async Task HandleAsync_ForARegisteredModule_CallsTheGateway_AndDeletesTheRow()
    {
        var fixture = await CreateFixtureAsync();

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(fixture.Modules.All);
        var call = Assert.Single(fixture.RegistrationGateway.RevokeCalls);
        Assert.Equal(Calendar, call.Module.ModuleKey);
        Assert.Equal(SiteId, call.Module.SiteId);
    }

    /// <summary>The ordering claim: nothing on this side is deleted unless the module confirms
    /// first - a failed revoke leaves both sides still agreeing the module is enabled, rather than
    /// leaving Chat's own row silently wrong.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheModuleRefuses_LeavesTheRowInPlace()
    {
        var fixture = await CreateFixtureAsync();
        fixture.RegistrationGateway.UnreachableOnRevoke = true;

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.RegistrationFailed", result.Error!.Value.Code);
        Assert.Single(fixture.Modules.All);
    }

    [Fact]
    public async Task HandleAsync_WithoutPermission_ReturnsForbidden_AndCallsNothing()
    {
        var fixture = await CreateFixtureAsync(permitted: false);

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(fixture.RegistrationGateway.RevokeCalls);
    }

    [Fact]
    public async Task HandleAsync_ForAModuleNotEnabledOnThisSite_ReturnsModuleNotEnabled()
    {
        var fixture = await CreateFixtureAsync(seeded: false);

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.NotEnabled", result.Error!.Value.Code);
        Assert.Empty(fixture.RegistrationGateway.RevokeCalls);
    }
}
