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

    private sealed record Fixture(
        EnableModuleForSiteAsOwnerHandler Handler, FakeEnabledModuleRepository Modules,
        FakeEnabledModuleReadStore ReadStore, FakeModuleRegistrationGateway RegistrationGateway,
        FakeModuleProvisioningSecretProvider ProvisioningSecrets, FakeModuleEntryPointProvider EntryPoints,
        FakeModulePermissionsProvider ModulePermissions, FakeRoleRepository Roles, FakeSiteRepository Sites);

    private static Fixture CreateFixture()
    {
        var modules = new FakeEnabledModuleRepository();
        var readStore = new FakeEnabledModuleReadStore();
        var registrationGateway = new FakeModuleRegistrationGateway();
        var provisioningSecrets = new FakeModuleProvisioningSecretProvider();
        var entryPoints = new FakeModuleEntryPointProvider();
        var modulePermissions = new FakeModulePermissionsProvider();
        var roles = new FakeRoleRepository();
        var sites = new FakeSiteRepository();
        sites.Seed(new Site(SiteId, "owner-grant-target", allowedOrigins: [], name: "Prospect Barbershop"));

        var handler = new EnableModuleForSiteAsOwnerHandler(
            modules, readStore, registrationGateway, provisioningSecrets, entryPoints, modulePermissions, roles,
            sites, new FakeClock(Now), new FakeIdGenerator());
        return new Fixture(
            handler, modules, readStore, registrationGateway, provisioningSecrets, entryPoints, modulePermissions,
            roles, sites);
    }

    private static Application.UseCases.EnableModuleForSiteAsOwner.EnableModuleForSiteAsOwner Command(
        DateTimeOffset? expiresAt) =>
        new(SiteId, "calendar", ["/booking"], ValidCredential, expiresAt);

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

    /// <summary>`23-65`/`adr/0150`'s own headline claim, proven at the Application level: nothing in
    /// <see cref="Application.UseCases.EnableModuleForSiteAsOwner.EnableModuleForSiteAsOwner"/> carries a
    /// caller-supplied secret at all (see this type's own <c>Command</c> helper - there is no field to
    /// pass one in), and the value the module-registration gateway actually receives is exactly the one
    /// <see cref="IModuleProvisioningSecretProvider"/> was configured with - proof the secret this
    /// handler uses comes from configuration, not from anything a caller could ever control.</summary>
    [Fact]
    public async Task HandleAsync_CallsTheRegistrationGateway_WithTheConfiguredSecret_NeverACallerSuppliedOne()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(Command(expiresAt: null), CancellationToken.None);

        var call = Assert.Single(fixture.RegistrationGateway.RegisterCalls);
        Assert.Equal(FakeModuleProvisioningSecretProvider.DefaultSecret, call.ProvisioningSecret.Value);
    }

    /// <summary>`adr/0150`'s own deployment-state case: this handler must refuse per call, not merely
    /// assume the secret exists - see <see cref="IModuleProvisioningSecretProvider"/>'s own remarks for
    /// why a whole-host boot failure is the wrong shape for an unconfigured owner-only feature.</summary>
    [Fact]
    public async Task HandleAsync_WhenNoProvisioningSecretIsConfigured_ReturnsProvisioningNotConfigured_AndGrantsNothing()
    {
        var fixture = CreateFixture();
        fixture.ProvisioningSecrets.Secret = null;

        var result = await fixture.Handler.HandleAsync(Command(expiresAt: null), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.ProvisioningNotConfigured", result.Error!.Value.Code);
        Assert.Empty(fixture.Modules.All);
        Assert.Empty(fixture.RegistrationGateway.RegisterCalls);
    }

    /// <summary>`23-92`/`adr/0154`'s own headline claim, proven at the Application level: nothing in
    /// <see cref="Application.UseCases.EnableModuleForSiteAsOwner.EnableModuleForSiteAsOwner"/> carries a
    /// caller-supplied entry point at all (see this type's own <c>Command</c> helper - there is no field
    /// to pass one in), and the address the module-registration gateway actually receives, and the one
    /// the persisted row carries, is exactly the one <see cref="IModuleEntryPointProvider"/> was
    /// configured with for this module's key.</summary>
    [Fact]
    public async Task HandleAsync_CallsTheRegistrationGateway_WithTheConfiguredEntryPoint_NeverACallerSuppliedOne()
    {
        var fixture = CreateFixture();
        var configuredEntryPoint = new Uri("https://calendar-really-lives-here.example.com");
        fixture.EntryPoints.Seed(new ModuleKey("calendar"), configuredEntryPoint);

        var result = await fixture.Handler.HandleAsync(Command(expiresAt: null), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var call = Assert.Single(fixture.RegistrationGateway.RegisterCalls);
        Assert.Equal(configuredEntryPoint, call.Module.EntryPoint);
        var saved = Assert.Single(fixture.Modules.All);
        Assert.Equal(configuredEntryPoint, saved.EntryPoint);
    }

    /// <summary>`23-92`'s own Done-when: a module this deployment has not declared an entry point for is
    /// refused with a message naming what is missing - never a blank that only fails later once the
    /// module is actually called (this item's own brief: "not a blank that fails later as a 404, which is
    /// precisely today's failure with an extra step").</summary>
    [Fact]
    public async Task HandleAsync_WhenNoEntryPointIsConfiguredForTheModule_ReturnsEntryPointNotConfigured_AndGrantsNothing()
    {
        var fixture = CreateFixture();
        fixture.EntryPoints.Clear();

        var result = await fixture.Handler.HandleAsync(Command(expiresAt: null), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.EntryPointNotConfigured", result.Error!.Value.Code);
        Assert.Contains("calendar", result.Error!.Value.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Modules.All);
        Assert.Empty(fixture.RegistrationGateway.RegisterCalls);
    }

    /// <summary>`23-102`'s own headline claim: granting a module does not merely record the entitlement,
    /// it also grows the site's own "Operator"/"Admin" roles by exactly the permissions this deployment
    /// declared for the module - the gap the backlog item's own report found: a granted site whose roles
    /// carry nothing a booking action, a booking screen, or a calendar-configuration screen checks.</summary>
    [Fact]
    public async Task HandleAsync_SeedsTheModulesPermissions_IntoTheSitesOperatorAndAdminRoles()
    {
        var fixture = CreateFixture();
        fixture.ModulePermissions.Seed(
            new ModuleKey("calendar"),
            new ModulePermissionSet(
                OperatorPermissions: ["booking:confirm", "booking:reject"], AdminPermissions: ["calendar:configure"]));

        var result = await fixture.Handler.HandleAsync(Command(expiresAt: null), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            new HashSet<string> { "booking:confirm", "booking:reject" },
            fixture.Roles.PermissionsFor(SiteId, "Operator"));
        Assert.Equal(new HashSet<string> { "calendar:configure" }, fixture.Roles.PermissionsFor(SiteId, "Admin"));
    }

    /// <summary>The other half of the same claim, proven the way a fails-before proof would: a module the
    /// deployment declared *no* extra permissions for adds nothing and still succeeds - the "empty is a
    /// legitimate answer" half of <c>IModulePermissionsProvider</c>'s own contract, unlike the entry
    /// point's hard refusal right above.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheModuleDeclaresNoExtraPermissions_GrantsTheModule_AndAddsNothingToEitherRole()
    {
        var fixture = CreateFixture();
        fixture.ModulePermissions.DefaultForEveryKey = ModulePermissionSet.Empty;

        var result = await fixture.Handler.HandleAsync(Command(expiresAt: null), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(fixture.Roles.PermissionsFor(SiteId, "Operator"));
        Assert.Empty(fixture.Roles.PermissionsFor(SiteId, "Admin"));
    }

    /// <summary>The acceptance test the author is about to run for real: re-granting a module a site
    /// already holds re-seeds the identical permissions rather than erroring or duplicating - proof that
    /// repairing one of the four sites this item's own backlog item names is exactly a re-grant, not a
    /// separate mechanism. <see cref="FakeRoleRepository.AddPermissionsAsync"/>'s own union-of-a-`HashSet`
    /// shape already makes a second identical add inert; this test is what pins that behaviour to this
    /// handler's own call site rather than merely to the fake.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheSiteAlreadyHoldsAPermissionTheModuleNeeds_AddsItOnlyOnce()
    {
        var fixture = CreateFixture();
        fixture.Roles.SeedPermissions(SiteId, "Operator", "booking:confirm");
        fixture.ModulePermissions.Seed(
            new ModuleKey("calendar"),
            new ModulePermissionSet(OperatorPermissions: ["booking:confirm", "booking:reject"], AdminPermissions: []));

        var result = await fixture.Handler.HandleAsync(Command(expiresAt: null), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            new HashSet<string> { "booking:confirm", "booking:reject" },
            fixture.Roles.PermissionsFor(SiteId, "Operator"));
    }
}
