using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.RotateModuleCredentialAsOwner;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RotateModuleCredentialAsOwner;

/// <summary>
/// `23-83`/`adr/0151`: the platform owner's own half of `22-11`'s "rotate without downtime" - the
/// identical behaviour <c>RotateModuleCredentialHandlerTests</c> proved for the deleted tenant handler
/// (module-first ordering, mint-don't-accept), minus the permission check that handler had and plus
/// the "provisioning secret not configured" case every owner-surface handler now has to answer
/// (`EnableModuleForSiteAsOwnerHandlerTests`' own sibling case).
/// </summary>
public class RotateModuleCredentialAsOwnerHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly ModuleKey Calendar = new("calendar");
    private static readonly Uri EntryPoint = new("https://calendar.example.com");
    private const string OriginalCredential = "original-secret-of-sixteen-plus-chars";

    private sealed record Fixture(
        RotateModuleCredentialAsOwnerHandler Handler, FakeEnabledModuleRepository Modules,
        FakeModuleRegistrationGateway RegistrationGateway, FakeModuleProvisioningSecretProvider ProvisioningSecrets,
        EnabledModuleId ExistingId);

    private static async Task<Fixture> CreateFixtureAsync(bool seeded = true)
    {
        var modules = new FakeEnabledModuleRepository();
        var registrationGateway = new FakeModuleRegistrationGateway();
        var provisioningSecrets = new FakeModuleProvisioningSecretProvider();

        var existingId = new EnabledModuleId(Guid.NewGuid());
        if (seeded)
        {
            var existing = new EnabledModule(
                existingId, SiteId, Calendar, ["/booking"], EntryPoint, new ModuleCredential(OriginalCredential), Now);
            await modules.SaveAsync(existing, CancellationToken.None);
        }

        var generator = new FixedModuleCredentialGenerator("freshly-minted-secret-of-sixteen-plus-x");
        var handler = new RotateModuleCredentialAsOwnerHandler(modules, registrationGateway, generator, provisioningSecrets);
        return new Fixture(handler, modules, registrationGateway, provisioningSecrets, existingId);
    }

    private static Application.UseCases.RotateModuleCredentialAsOwner.RotateModuleCredentialAsOwner Command() =>
        new(SiteId, Calendar.Value);

    /// <summary>The end-to-end claim this item's own report has to demonstrate: no permission checker
    /// exists on this handler at all (constructor signature carries none), and the rotation still
    /// lands - proving the sole gate is the route's own RequirePlatformOwner policy.</summary>
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
        Assert.Equal(new ModuleProvisioningSecret(FakeModuleProvisioningSecretProvider.DefaultSecret), call.ProvisioningSecret);
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
    public async Task HandleAsync_ForAModuleNotEnabledOnThisSite_ReturnsModuleNotEnabled()
    {
        var fixture = await CreateFixtureAsync(seeded: false);

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.NotEnabled", result.Error!.Value.Code);
        Assert.Empty(fixture.RegistrationGateway.RotateCalls);
    }

    /// <summary>`adr/0150`'s own deployment-state case, extended to this second owner caller: a host
    /// with no configured secret refuses per call rather than minting anything.</summary>
    [Fact]
    public async Task HandleAsync_WhenNoProvisioningSecretIsConfigured_ReturnsModuleProvisioningNotConfigured()
    {
        var fixture = await CreateFixtureAsync();
        fixture.ProvisioningSecrets.Secret = null;

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.ProvisioningNotConfigured", result.Error!.Value.Code);
        Assert.Empty(fixture.RegistrationGateway.RotateCalls);
        var stored = Assert.Single(fixture.Modules.All);
        Assert.Equal(new ModuleCredential(OriginalCredential), stored.Credential);
    }

    private sealed class FixedModuleCredentialGenerator(string value) : IModuleCredentialGenerator
    {
        public string NewCredential() => value;
    }
}
