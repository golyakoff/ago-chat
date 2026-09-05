using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.RevokeModuleForSiteAsOwner;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RevokeModuleForSiteAsOwner;

/// <summary>`22-17`'s own revocation Done-when at the Application level: "an entitlement that cannot
/// be taken back is not an entitlement." `23-13` adds the asymmetry: revoking a tenant's own
/// self-service purchase now needs a stated reason and a stated force; revoking a grant the owner made
/// needs neither, unchanged from before this item.</summary>
public class RevokeModuleForSiteAsOwnerHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly ModuleKey Calendar = new("calendar");
    private static readonly Uri EntryPoint = new("https://calendar.example.com");
    private const string ValidProvisioningSecret = "a-provisioning-secret-of-sixteen-plus-chars";
    private const string PlatformOwnerSubject = "keycloak-sub-of-the-platform-owner";
    private const string ValidReason = "Tenant is under active law-enforcement investigation; module access must stop immediately.";

    private sealed record Fixture(
        RevokeModuleForSiteAsOwnerHandler Handler, FakeEnabledModuleRepository Modules,
        FakeModuleRegistrationGateway RegistrationGateway, FakeModuleRevokeOverrideRepository Overrides);

    private static async Task<Fixture> CreateFixtureAsync(bool seeded = true, bool grantedByOwner = true)
    {
        var modules = new FakeEnabledModuleRepository();
        var registrationGateway = new FakeModuleRegistrationGateway();
        var overrides = new FakeModuleRevokeOverrideRepository();

        if (seeded)
        {
            var existing = new EnabledModule(
                new EnabledModuleId(Guid.NewGuid()), SiteId, Calendar, ["/booking"], EntryPoint,
                new ModuleCredential("a-shared-secret-of-sixteen-plus-chars"), Now, grantedByOwner, expiresAt: null);
            await modules.SaveAsync(existing, CancellationToken.None);
        }

        var handler = new RevokeModuleForSiteAsOwnerHandler(
            modules, registrationGateway, overrides, new FakeClock(Now), new FakeIdGenerator());
        return new Fixture(handler, modules, registrationGateway, overrides);
    }

    private static Application.UseCases.RevokeModuleForSiteAsOwner.RevokeModuleForSiteAsOwner Command(
        bool force = false, string? reason = null) =>
        new(SiteId, Calendar.Value, ValidProvisioningSecret, PlatformOwnerSubject, force, reason);

    [Fact]
    public async Task HandleAsync_ForAnOwnerGrant_WithNoForce_CallsTheGateway_AndDeletesTheRow()
    {
        var fixture = await CreateFixtureAsync(grantedByOwner: true);

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(fixture.Modules.All);
        var call = Assert.Single(fixture.RegistrationGateway.RevokeCalls);
        Assert.Equal(Calendar, call.Module.ModuleKey);
        Assert.Equal(SiteId, call.Module.SiteId);
    }

    /// <summary>`23-13`'s own Done-when: "revoking an owner-granted module is unchanged and writes no
    /// override record" - proven here by asserting the override repository stays empty, not merely by
    /// the call succeeding.</summary>
    [Fact]
    public async Task HandleAsync_ForAnOwnerGrant_WithNoForce_WritesNoOverrideRecord()
    {
        var fixture = await CreateFixtureAsync(grantedByOwner: true);

        await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.Empty(fixture.Overrides.Records);
    }

    /// <summary>The regression `23-13` exists to fix: `22-17` shipped this as
    /// "HandleAsync_RevokesASelfServicePurchase_JustAsReadilyAsAnOwnerGrant" - the same call, with no
    /// force, against a tenant's own purchase must now be refused rather than succeed.</summary>
    [Fact]
    public async Task HandleAsync_ForASelfServicePurchase_WithNoForce_IsRefused_AndLeavesTheRowInPlace()
    {
        var fixture = await CreateFixtureAsync(grantedByOwner: false);

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.RevokePurchaseRequiresForce", result.Error!.Value.Code);
        Assert.Single(fixture.Modules.All);
        Assert.Empty(fixture.RegistrationGateway.RevokeCalls);
        Assert.Empty(fixture.Overrides.Records);
    }

    [Fact]
    public async Task HandleAsync_ForASelfServicePurchase_WithForceAndAReason_Succeeds_AndRecordsExactlyOneOverride()
    {
        var fixture = await CreateFixtureAsync(grantedByOwner: false);

        var result = await fixture.Handler.HandleAsync(Command(force: true, reason: ValidReason), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(fixture.Modules.All);
        Assert.Single(fixture.RegistrationGateway.RevokeCalls);

        var recorded = Assert.Single(fixture.Overrides.Records);
        Assert.Equal(SiteId, recorded.SiteId);
        Assert.Equal(Calendar.Value, recorded.ModuleKey);
        Assert.Equal(PlatformOwnerSubject, recorded.RevokedBy);
        Assert.Equal(ValidReason, recorded.Reason);
        Assert.Equal(Now, recorded.RevokedAt);
    }

    [Fact]
    public async Task HandleAsync_WithForce_ButNoReason_IsRefused_BeforeTouchingTheModuleOrTheRow()
    {
        var fixture = await CreateFixtureAsync(grantedByOwner: false);

        var result = await fixture.Handler.HandleAsync(Command(force: true, reason: null), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.RevokeReasonRequired", result.Error!.Value.Code);
        Assert.Single(fixture.Modules.All);
        Assert.Empty(fixture.RegistrationGateway.RevokeCalls);
        Assert.Empty(fixture.Overrides.Records);
    }

    [Fact]
    public async Task HandleAsync_WithForce_AndABlankReason_IsRefused_TheSameAsNoReasonAtAll()
    {
        var fixture = await CreateFixtureAsync(grantedByOwner: false);

        var result = await fixture.Handler.HandleAsync(Command(force: true, reason: "   "), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.RevokeReasonRequired", result.Error!.Value.Code);
        Assert.Empty(fixture.RegistrationGateway.RevokeCalls);
    }

    [Fact]
    public async Task HandleAsync_WithForce_AndAnOverlongReason_IsRefused()
    {
        var fixture = await CreateFixtureAsync(grantedByOwner: false);
        var overlong = new string('x', RevokeModuleForSiteAsOwnerHandler.MaxReasonLength + 1);

        var result = await fixture.Handler.HandleAsync(Command(force: true, reason: overlong), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.RevokeReasonRequired", result.Error!.Value.Code);
        Assert.Empty(fixture.RegistrationGateway.RevokeCalls);
    }

    /// <summary>Force set against a grant the owner made is accepted (no new ceremony on that path -
    /// this type's own remarks) but writes nothing: nothing was overridden, so there is nothing to
    /// attest to.</summary>
    [Fact]
    public async Task HandleAsync_WithForceAndAReason_ForAnOwnerGrant_Succeeds_ButWritesNoOverrideRecord()
    {
        var fixture = await CreateFixtureAsync(grantedByOwner: true);

        var result = await fixture.Handler.HandleAsync(Command(force: true, reason: ValidReason), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(fixture.Modules.All);
        Assert.Empty(fixture.Overrides.Records);
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

    /// <summary>The force/reason override is not written if the module-registration gateway refuses
    /// the revoke - the same "nothing committed, nothing to attest to" shape
    /// <see cref="HandleAsync_WhenTheModuleRefuses_LeavesTheRowInPlace"/> already proves for the row
    /// itself.</summary>
    [Fact]
    public async Task HandleAsync_ForcedButTheModuleRefuses_WritesNoOverrideRecord()
    {
        var fixture = await CreateFixtureAsync(grantedByOwner: false);
        fixture.RegistrationGateway.UnreachableOnRevoke = true;

        var result = await fixture.Handler.HandleAsync(Command(force: true, reason: ValidReason), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Single(fixture.Modules.All);
        Assert.Empty(fixture.Overrides.Records);
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
