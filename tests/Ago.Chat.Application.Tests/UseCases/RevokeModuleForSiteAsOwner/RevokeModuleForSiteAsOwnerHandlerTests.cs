using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.RevokeModuleForSiteAsOwner;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RevokeModuleForSiteAsOwner;

/// <summary>`22-17`'s own revocation Done-when at the Application level: "an entitlement that cannot
/// be taken back is not an entitlement."</summary>
public class RevokeModuleForSiteAsOwnerHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly ModuleKey Calendar = new("calendar");
    private static readonly Uri EntryPoint = new("https://calendar.example.com");
    private const string ValidProvisioningSecret = "a-provisioning-secret-of-sixteen-plus-chars";

    private sealed record Fixture(
        RevokeModuleForSiteAsOwnerHandler Handler, FakeEnabledModuleRepository Modules,
        FakeModuleRegistrationGateway RegistrationGateway);

    private static async Task<Fixture> CreateFixtureAsync(bool seeded = true, bool grantedByOwner = true)
    {
        var modules = new FakeEnabledModuleRepository();
        var registrationGateway = new FakeModuleRegistrationGateway();

        if (seeded)
        {
            var existing = new EnabledModule(
                new EnabledModuleId(Guid.NewGuid()), SiteId, Calendar, ["/booking"], EntryPoint,
                new ModuleCredential("a-shared-secret-of-sixteen-plus-chars"), Now, grantedByOwner, expiresAt: null);
            await modules.SaveAsync(existing, CancellationToken.None);
        }

        var handler = new RevokeModuleForSiteAsOwnerHandler(modules, registrationGateway);
        return new Fixture(handler, modules, registrationGateway);
    }

    private static Application.UseCases.RevokeModuleForSiteAsOwner.RevokeModuleForSiteAsOwner Command() =>
        new(SiteId, Calendar.Value, ValidProvisioningSecret);

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

    /// <summary>The owner can revoke a tenant's own self-service purchase too, not only a grant it
    /// made itself - see the handler's own remarks on why this is deliberate.</summary>
    [Fact]
    public async Task HandleAsync_RevokesASelfServicePurchase_JustAsReadilyAsAnOwnerGrant()
    {
        var fixture = await CreateFixtureAsync(grantedByOwner: false);

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(fixture.Modules.All);
    }

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
    public async Task HandleAsync_ForAModuleNotEnabledOnThisSite_ReturnsModuleNotEnabled()
    {
        var fixture = await CreateFixtureAsync(seeded: false);

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.NotEnabled", result.Error!.Value.Code);
        Assert.Empty(fixture.RegistrationGateway.RevokeCalls);
    }
}
