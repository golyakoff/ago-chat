using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.EnableModuleForSiteAsOwner;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.EnableModuleForSiteAsOwner;

/// <summary>
/// `22-17`'s own Done-when at the Application level: the platform owner can grant a module with no
/// payment, the grant is recorded distinguishably from a self-service one
/// (<see cref="EnabledModule.GrantedByOwner"/>), and the expiry decision is enforced rather than
/// merely accepted.
/// </summary>
public class EnableModuleForSiteAsOwnerHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private const string ValidCredential = "a-shared-secret-of-sixteen-plus-chars";
    private const string ValidProvisioningSecret = "a-provisioning-secret-of-sixteen-plus-chars";

    private sealed record Fixture(
        EnableModuleForSiteAsOwnerHandler Handler, FakeEnabledModuleRepository Modules,
        FakeEnabledModuleReadStore ReadStore, FakeModuleRegistrationGateway RegistrationGateway,
        FakeSiteRepository Sites);

    private static Fixture CreateFixture()
    {
        var modules = new FakeEnabledModuleRepository();
        var readStore = new FakeEnabledModuleReadStore();
        var registrationGateway = new FakeModuleRegistrationGateway();
        var sites = new FakeSiteRepository();
        sites.Seed(new Site(SiteId, "owner-grant-target", allowedOrigins: [], name: "Prospect Barbershop"));

        var handler = new EnableModuleForSiteAsOwnerHandler(
            modules, readStore, registrationGateway, sites, new FakeClock(Now), new FakeIdGenerator());
        return new Fixture(handler, modules, readStore, registrationGateway, sites);
    }

    private static Application.UseCases.EnableModuleForSiteAsOwner.EnableModuleForSiteAsOwner Command(
        DateTimeOffset? expiresAt) =>
        new(SiteId, "calendar", ["/booking"], "https://calendar.example.com", ValidCredential, ValidProvisioningSecret,
            expiresAt);

    /// <summary>The end-to-end claim this item's own report has to demonstrate: no permission checker
    /// exists on this handler at all (constructor signature), and the write still lands - proving the
    /// sole gate is the route's own RequirePlatformOwner policy, not a second, weaker copy of it here.</summary>
    [Fact]
    public async Task HandleAsync_WithNoExpiry_GrantsThePermanentGrant_MarkedAsGrantedByOwner()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(expiresAt: null), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var saved = Assert.Single(fixture.Modules.All);
        Assert.True(saved.GrantedByOwner);
        Assert.Null(saved.ExpiresAt);
    }

    [Fact]
    public async Task HandleAsync_WithAFutureExpiry_GrantsATrial_CarryingThatExpiry()
    {
        var fixture = CreateFixture();
        var expiresAt = Now.AddDays(30);

        var result = await fixture.Handler.HandleAsync(Command(expiresAt), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var saved = Assert.Single(fixture.Modules.All);
        Assert.True(saved.GrantedByOwner);
        Assert.Equal(expiresAt, saved.ExpiresAt);
    }

    /// <summary>Fails-before: before this guard existed, a caller passing "yesterday" would have
    /// reached EnabledModule's own constructor and thrown an unhandled ArgumentException instead of a
    /// clean Result failure - see this item's own report for the captured failure text.</summary>
    [Fact]
    public async Task HandleAsync_WithAnExpiryInThePast_ReturnsGrantExpiryInvalid_AndGrantsNothing()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(Now.AddSeconds(-1)), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.GrantExpiryInvalid", result.Error!.Value.Code);
        Assert.Empty(fixture.Modules.All);
        Assert.Empty(fixture.RegistrationGateway.RegisterCalls);
    }

    [Fact]
    public async Task HandleAsync_WithAnExpiryExactlyNow_ReturnsGrantExpiryInvalid()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(Now), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.GrantExpiryInvalid", result.Error!.Value.Code);
    }

    /// <summary>This item's own second brief question ("must not become the normal path") answered in
    /// code, not merely in prose: an owner cannot grant an unbounded-looking trial by typing a date far
    /// enough out to be indistinguishable from forever - they have to actually choose no expiry.</summary>
    [Fact]
    public async Task HandleAsync_WithAnExpiryBeyondTheMaxGrantDuration_ReturnsGrantExpiryInvalid()
    {
        var fixture = CreateFixture();
        var tooFar = Now + EnableModuleForSiteAsOwnerHandler.MaxGrantDuration + TimeSpan.FromDays(1);

        var result = await fixture.Handler.HandleAsync(Command(tooFar), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.GrantExpiryInvalid", result.Error!.Value.Code);
        Assert.Empty(fixture.Modules.All);
    }

    [Fact]
    public async Task HandleAsync_AtExactlyTheMaxGrantDuration_Succeeds()
    {
        var fixture = CreateFixture();
        var atTheLimit = Now + EnableModuleForSiteAsOwnerHandler.MaxGrantDuration;

        var result = await fixture.Handler.HandleAsync(Command(atTheLimit), CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task HandleAsync_WhenTheModuleRefuses_GrantsNothing()
    {
        var fixture = CreateFixture();
        fixture.RegistrationGateway.UnreachableOnRegister = true;

        var result = await fixture.Handler.HandleAsync(Command(expiresAt: null), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.RegistrationFailed", result.Error!.Value.Code);
        Assert.Empty(fixture.Modules.All);
    }

    /// <summary>The same trigger-conflict rule the self-service handler enforces - an owner-granted
    /// module is not exempt from the rule that keeps routing unambiguous.</summary>
    [Fact]
    public async Task HandleAsync_WhenATriggerWordAlreadyBelongsToAnotherEnabledModule_IsRejected()
    {
        var fixture = CreateFixture();
        fixture.ReadStore.Seed(
            SiteId, new EnabledModuleSummary(
                new ModuleKey("faq"), ["/booking"], new Uri("https://faq.example.com"),
                new ModuleCredential(ValidCredential), GrantedByOwner: false, ExpiresAt: null));

        var result = await fixture.Handler.HandleAsync(Command(expiresAt: null), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.TriggerWordAlreadyRegistered", result.Error!.Value.Code);
    }

    /// <summary>The provisioning call carries a real display name pulled from the tenant's own Site,
    /// not a placeholder - proof this handler reuses `22-11`'s own mechanism rather than a stripped-down
    /// copy of it.</summary>
    [Fact]
    public async Task HandleAsync_PassesTheSitesOwnDisplayName_ToTheRegistrationGateway()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(Command(expiresAt: null), CancellationToken.None);

        var call = Assert.Single(fixture.RegistrationGateway.RegisterCalls);
        Assert.Equal("Prospect Barbershop", call.DisplayName);
    }
}
