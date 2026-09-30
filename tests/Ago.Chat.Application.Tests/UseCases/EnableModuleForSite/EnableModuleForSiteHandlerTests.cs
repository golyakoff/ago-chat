using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.EnableModuleForSite;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.EnableModuleForSite;

/// <summary>
/// `26-316`'s own Done-when at the Application level: a tenant admin holding `site:configure` can turn a
/// module on for their own site (option в, self-serve), the row is recorded as a self-service grant
/// (<see cref="EnabledModule.GrantedByOwner"/> <see langword="false"/>, no expiry), the credential is
/// minted rather than caller-supplied, and the call refuses without the permission and never reaches a
/// site the caller does not administer.
/// </summary>
public class EnableModuleForSiteHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId Admin = new(Guid.NewGuid());

    private sealed record Fixture(
        EnableModuleForSiteHandler Handler, FakePermissionChecker Permissions, FakeEnabledModuleRepository Modules,
        FakeEnabledModuleReadStore ReadStore, FakeModuleRegistrationGateway RegistrationGateway,
        FakeModuleProvisioningSecretProvider ProvisioningSecrets, FakeModuleEntryPointProvider EntryPoints,
        FakeModulePermissionsProvider ModulePermissions, FakeModuleCredentialGenerator Credentials,
        FakeRoleRepository Roles, FakeSiteRepository Sites);

    private static Fixture CreateFixture(bool granted = true)
    {
        var permissions = new FakePermissionChecker();
        if (granted)
        {
            permissions.Grant(Admin, SiteId, Permission.SiteConfigure);
        }

        var modules = new FakeEnabledModuleRepository();
        var readStore = new FakeEnabledModuleReadStore();
        var registrationGateway = new FakeModuleRegistrationGateway();
        var provisioningSecrets = new FakeModuleProvisioningSecretProvider();
        var entryPoints = new FakeModuleEntryPointProvider();
        var modulePermissions = new FakeModulePermissionsProvider();
        var credentials = new FakeModuleCredentialGenerator();
        var roles = new FakeRoleRepository();
        var sites = new FakeSiteRepository();
        sites.Seed(new Site(SiteId, "self-serve-target", allowedOrigins: [], name: "Topaz Salon"));

        var handler = new EnableModuleForSiteHandler(
            permissions, modules, readStore, registrationGateway, provisioningSecrets, entryPoints, modulePermissions,
            credentials, roles, sites, new FakeClock(Now), new FakeIdGenerator());
        return new Fixture(
            handler, permissions, modules, readStore, registrationGateway, provisioningSecrets, entryPoints,
            modulePermissions, credentials, roles, sites);
    }

    private static Application.UseCases.EnableModuleForSite.EnableModuleForSite Command(SiteId? siteId = null) =>
        new(Admin, siteId ?? SiteId, "calendar", ["/записаться"]);

    [Fact]
    public async Task HandleAsync_WithPermission_EnablesTheModule_AsASelfServiceGrant_WithNoExpiry()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var saved = Assert.Single(fixture.Modules.All);
        Assert.False(saved.GrantedByOwner);
        Assert.Null(saved.ExpiresAt);
        Assert.Equal(["/записаться"], saved.TriggerWords);
        Assert.Single(fixture.RegistrationGateway.RegisterCalls);
    }

    [Fact]
    public async Task HandleAsync_WithoutThePermission_ReturnsForbidden_AndEnablesNothing()
    {
        var fixture = CreateFixture(granted: false);

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(fixture.Modules.All);
        Assert.Empty(fixture.RegistrationGateway.RegisterCalls);
    }

    /// <summary>"Only ever affects the caller's own site" (the item's own Done-when): permission is held on
    /// <see cref="SiteId"/>, but a command naming a different site is refused - the handler gates on the
    /// command's own site, so a caller cannot enable a module for a tenant they do not administer.</summary>
    [Fact]
    public async Task HandleAsync_ForASiteTheCallerDoesNotAdminister_ReturnsForbidden_AndEnablesNothing()
    {
        var fixture = CreateFixture();
        var someoneElsesSite = new SiteId(Guid.NewGuid());

        var result = await fixture.Handler.HandleAsync(Command(someoneElsesSite), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(fixture.Modules.All);
        Assert.Empty(fixture.RegistrationGateway.RegisterCalls);
    }

    /// <summary>Unlike the owner grant, a tenant supplies no credential: the one the module-registration
    /// gateway receives, and the one the persisted row carries, is exactly what the generator minted.</summary>
    [Fact]
    public async Task HandleAsync_MintsTheCredential_FromTheGenerator_NeverACallerSuppliedOne()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        var call = Assert.Single(fixture.RegistrationGateway.RegisterCalls);
        Assert.Equal(fixture.Credentials.Last, call.Credential.Value);
        var saved = Assert.Single(fixture.Modules.All);
        Assert.Equal(fixture.Credentials.Last, saved.Credential.Value);
    }

    /// <summary>The provisioning call carries the site's own display name and the configured secret, proving
    /// this reuses `22-11`'s module-first mechanism rather than a stripped-down copy.</summary>
    [Fact]
    public async Task HandleAsync_CallsTheGateway_WithTheSitesDisplayName_AndTheConfiguredSecret()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        var call = Assert.Single(fixture.RegistrationGateway.RegisterCalls);
        Assert.Equal("Topaz Salon", call.DisplayName);
        Assert.Equal(FakeModuleProvisioningSecretProvider.DefaultSecret, call.ProvisioningSecret.Value);
    }

    /// <summary>`23-102`'s seeding, on the self-serve path too - or the admin enables the calendar and finds
    /// nobody, themselves included, holds `calendar:configure` to use it.</summary>
    [Fact]
    public async Task HandleAsync_SeedsTheModulesPermissions_IntoTheSitesOperatorAndAdminRoles()
    {
        var fixture = CreateFixture();
        fixture.ModulePermissions.Seed(
            new ModuleKey("calendar"),
            new ModulePermissionSet(
                OperatorPermissions: ["booking:confirm", "booking:reject"], AdminPermissions: ["calendar:configure"]));

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            new HashSet<string> { "booking:confirm", "booking:reject" },
            fixture.Roles.PermissionsFor(SiteId, "Operator"));
        Assert.Equal(new HashSet<string> { "calendar:configure" }, fixture.Roles.PermissionsFor(SiteId, "Admin"));
    }

    /// <summary>A double-clicked toggle: the module is already active for this site, so a repeat enable
    /// returns the existing row's id and mints no second registration.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheModuleIsAlreadyActive_IsIdempotent_AndDoesNotReRegister()
    {
        var fixture = CreateFixture();
        var existing = new EnabledModule(
            new EnabledModuleId(Guid.NewGuid()), SiteId, new ModuleKey("calendar"), ["/записаться"],
            new Uri("https://calendar.example.com"), new ModuleCredential("an-existing-secret-of-sixteen-plus"), Now);
        await fixture.Modules.SaveAsync(existing, CancellationToken.None);
        fixture.ReadStore.Seed(
            SiteId, new EnabledModuleSummary(
                new ModuleKey("calendar"), ["/записаться"], existing.EntryPoint, existing.Credential,
                GrantedByOwner: false, ExpiresAt: null));

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(existing.Id, result.Value);
        Assert.Single(fixture.Modules.All);
        Assert.Empty(fixture.RegistrationGateway.RegisterCalls);
    }

    [Fact]
    public async Task HandleAsync_WhenTheModuleRefuses_EnablesNothing()
    {
        var fixture = CreateFixture();
        fixture.RegistrationGateway.UnreachableOnRegister = true;

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.RegistrationFailed", result.Error!.Value.Code);
        Assert.Empty(fixture.Modules.All);
    }

    [Fact]
    public async Task HandleAsync_WhenNoEntryPointIsConfigured_ReturnsEntryPointNotConfigured_AndEnablesNothing()
    {
        var fixture = CreateFixture();
        fixture.EntryPoints.Clear();

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.EntryPointNotConfigured", result.Error!.Value.Code);
        Assert.Empty(fixture.Modules.All);
        Assert.Empty(fixture.RegistrationGateway.RegisterCalls);
    }

    [Fact]
    public async Task HandleAsync_WhenNoProvisioningSecretIsConfigured_ReturnsProvisioningNotConfigured_AndEnablesNothing()
    {
        var fixture = CreateFixture();
        fixture.ProvisioningSecrets.Secret = null;

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.ProvisioningNotConfigured", result.Error!.Value.Code);
        Assert.Empty(fixture.Modules.All);
    }

    [Fact]
    public async Task HandleAsync_WhenATriggerWordBelongsToAnotherEnabledModule_IsRejected()
    {
        var fixture = CreateFixture();
        fixture.ReadStore.Seed(
            SiteId, new EnabledModuleSummary(
                new ModuleKey("faq"), ["/записаться"], new Uri("https://faq.example.com"),
                new ModuleCredential("a-faq-secret-of-sixteen-plus-chars"), GrantedByOwner: false, ExpiresAt: null));

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.TriggerWordAlreadyRegistered", result.Error!.Value.Code);
        Assert.Empty(fixture.Modules.All);
    }
}
