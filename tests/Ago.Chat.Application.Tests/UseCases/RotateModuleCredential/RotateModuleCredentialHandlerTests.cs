using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.RotateModuleCredential;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RotateModuleCredential;

/// <summary>`22-11`'s own second Done-when, at the Application level.</summary>
public class RotateModuleCredentialHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly ModuleKey Calendar = new("calendar");
    private static readonly Uri EntryPoint = new("https://calendar.example.com");
    private const string OriginalCredential = "original-secret-of-sixteen-plus-chars";
    private const string ValidProvisioningSecret = "a-provisioning-secret-of-sixteen-plus-chars";

    private sealed record Fixture(
        RotateModuleCredentialHandler Handler, FakeEnabledModuleRepository Modules, FakePermissionChecker Permissions,
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
                existingId, SiteId, Calendar, ["/booking"], EntryPoint, new ModuleCredential(OriginalCredential), Now);
            await modules.SaveAsync(existing, CancellationToken.None);
        }

        var generator = new FixedModuleCredentialGenerator("freshly-minted-secret-of-sixteen-plus-x");
        var handler = new RotateModuleCredentialHandler(modules, permissions, registrationGateway, generator);
        return new Fixture(handler, modules, permissions, registrationGateway, existingId);
    }

    private static Application.UseCases.RotateModuleCredential.RotateModuleCredential Command() =>
        new(OperatorId, SiteId, Calendar.Value, ValidProvisioningSecret);

    [Fact]
    public async Task HandleAsync_ForARegisteredModule_CallsTheGateway_AndUpdatesTheStoredCredential()
    {
        var fixture = await CreateFixtureAsync();

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("freshly-minted-secret-of-sixteen-plus-x", result.Value.NewCredential.Value);
        var stored = Assert.Single(fixture.Modules.All);
        Assert.Equal(fixture.ExistingId, stored.Id);
        Assert.Equal(new ModuleCredential("freshly-minted-secret-of-sixteen-plus-x"), stored.Credential);

        var call = Assert.Single(fixture.RegistrationGateway.RotateCalls);
        Assert.Equal(Calendar, call.Module.ModuleKey);
        Assert.Equal(SiteId, call.Module.SiteId);
        Assert.Equal(EntryPoint, call.Module.EntryPoint);
        Assert.Equal(new ModuleCredential("freshly-minted-secret-of-sixteen-plus-x"), call.NewCredential);
        Assert.Equal(new ModuleProvisioningSecret(ValidProvisioningSecret), call.ProvisioningSecret);
    }

    /// <summary>The ordering claim this handler's own remarks make: nothing on this side changes
    /// unless the module confirms first.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheModuleRefuses_LeavesTheStoredCredentialUnchanged()
    {
        var fixture = await CreateFixtureAsync();
        fixture.RegistrationGateway.UnreachableOnRotate = true;

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.RegistrationFailed", result.Error!.Value.Code);
        var stored = Assert.Single(fixture.Modules.All);
        Assert.Equal(new ModuleCredential(OriginalCredential), stored.Credential);
    }

    [Fact]
    public async Task HandleAsync_WithoutPermission_ReturnsForbidden_AndCallsNothing()
    {
        var fixture = await CreateFixtureAsync(permitted: false);

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(fixture.RegistrationGateway.RotateCalls);
    }

    [Fact]
    public async Task HandleAsync_ForAModuleNotEnabledOnThisSite_ReturnsModuleNotEnabled()
    {
        var fixture = await CreateFixtureAsync(seeded: false);

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.NotEnabled", result.Error!.Value.Code);
        Assert.Empty(fixture.RegistrationGateway.RotateCalls);
    }

    private sealed class FixedModuleCredentialGenerator(string value) : IModuleCredentialGenerator
    {
        public string NewCredential() => value;
    }
}
