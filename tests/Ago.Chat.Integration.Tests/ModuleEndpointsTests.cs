using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Modules;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ListEnabledModulesForSite;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `19-03`/`22-11` built `PUT`/rotate/revoke/verify on `/api/v1/sites/{siteId}/modules`; `23-83`/`adr/0151`
/// removed all four. `26-316` (author decision в, self-serve) brings back enable and disable as a tenant
/// admin's own on/off toggle - now keyed by module in the path (`PUT`/`DELETE .../modules/{moduleKey}`),
/// safe because `adr/0150`/`adr/0154` moved the provisioning secret and entry point to configuration, so
/// nothing secret rides in the request. Rotate and verify stay gone from the tenant surface (owner-only).
///
/// <para>This file proves: the read still works as `23-01` left it; enable then disable toggles what the
/// read returns end-to-end; the toggle refuses without `site:configure` and never reaches a site the
/// caller does not administer; a platform-owner grant cannot be turned off from here; and rotate/verify
/// are still absent (a `404`, no route to reach at all).</para>
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class ModuleEndpointsTests(OperatorOidcFixture fixture)
{
    private string Route => $"/api/v1/sites/{fixture.SeededSiteId.Value}/modules";

    // ------------------------------------------------------------------------------------------
    // The surviving read - `23-01`'s own proof, unchanged in substance by `23-83`.
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// `23-01`: the fix itself, over real HTTP. Before that item, <c>HandleGetAsync</c> read
    /// <c>IEnabledModuleReadStore</c> straight from the endpoint with the route's <c>siteId</c>
    /// compared against nothing, so any authenticated operator of any site could list another
    /// tenant's enabled modules - including <c>EntryPoint</c>, <c>GrantedByOwner</c> and
    /// <c>ExpiresAt</c> - by naming its <c>siteId</c> in the URL. <c>demo-admin</c> genuinely holds
    /// <c>site:configure</c>, just not on the victim's site, the same "privileged caller, wrong
    /// tenant" shape <see cref="CrossTenantRouteIsolationTests"/> uses throughout - a refusal from an
    /// operator holding no permissions anywhere would prove nothing about tenant scoping
    /// specifically. Seeded directly against Postgres, not through a self-service `PUT` -
    /// `23-83` removed that route; a real row is written the way `OwnerModuleEndpointsTests`'
    /// own cross-tenant-victim setup already does.
    /// </summary>
    [Fact]
    public async Task DemoAdminToken_CannotListAnotherTenantsEnabledModules()
    {
        var victimSiteId = new SiteId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(victimSiteId, $"site_{victimSiteId.Value:N}", []));
            db.EnabledModules.Add(new EnabledModule(
                new EnabledModuleId(Guid.NewGuid()), victimSiteId, new ModuleKey("victim-faq"), ["/victim"],
                new Uri("https://victim.example.com"), new ModuleCredential("a-victim-secret-of-sixteen-plus-chars"),
                DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var response = await client.GetAsync($"/api/v1/sites/{victimSiteId.Value}/modules");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // The readable half - a real RFC 7807 problem body, the same shape OperatorInviteEndpointTests
        // reads: `title` (and `type`) carry ConversationErrors' own stable code, `Conversation.Forbidden`
        // here - never a JSON `code` field, which this vocabulary does not have.
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("Conversation.Forbidden", problem.Title);
    }

    /// <summary>The other half of the same proof: the identical caller, naming their <b>own</b> site,
    /// still succeeds - so the 403 above is "this site, not you", not this server refusing everyone
    /// or this route having quietly stopped working.</summary>
    [Fact]
    public async Task DemoAdminToken_CanStillListTheirOwnSitesEnabledModules()
    {
        await using (var db = fixture.CreateDbContext())
        {
            db.EnabledModules.Add(new EnabledModule(
                new EnabledModuleId(Guid.NewGuid()), fixture.SeededSiteId, new ModuleKey("faq-own-site-read-test"),
                ["/faq-own-site-read-test"], new Uri("https://faq.example.com"),
                new ModuleCredential("a-shared-secret-of-sixteen-plus-chars"), DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ModuleEndpoints.EnabledModulesResponse>();
        Assert.Contains(body!.Modules, m => m.ModuleKey == "faq-own-site-read-test");
    }

    [Fact]
    public async Task NoToken_CannotListModules()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token: null);

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ------------------------------------------------------------------------------------------
    // `26-316`: the self-serve toggle, end-to-end over real HTTP + real Postgres. The external module
    // deployment is stubbed (it lives in another repository), everything else is the production wiring.
    // ------------------------------------------------------------------------------------------

    /// <summary>The item's own headline: enabling a module for the caller's own site makes it appear in
    /// the same `GET` listing the console reads - a tenant admin turned a product on with no
    /// platform-owner action.</summary>
    [Fact]
    public async Task DemoAdminToken_CanEnableAModuleForTheirOwnSite_ThenItAppearsInTheListing()
    {
        var moduleKey = UniqueModuleKey();
        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var enable = await client.PutAsJsonAsync($"{Route}/{moduleKey}", new { triggerWords = new[] { $"/{moduleKey}" } });
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);

        var listing = await client.GetFromJsonAsync<ModuleEndpoints.EnabledModulesResponse>(Route);
        var enabled = Assert.Single(listing!.Modules, m => m.ModuleKey == moduleKey);
        Assert.False(enabled.GrantedByOwner);
    }

    /// <summary>The toggle's other half: disable removes the module from the same listing (`GetForSiteAsync`
    /// stops returning it), and does so non-destructively - the row is tombstoned in the database, not
    /// deleted, so a later re-enable and the erasure job can both still find the module's history.</summary>
    [Fact]
    public async Task DemoAdminToken_CanDisableAModule_ThenItDisappearsFromTheListing()
    {
        var moduleKey = UniqueModuleKey();
        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        await client.PutAsJsonAsync($"{Route}/{moduleKey}", new { triggerWords = new[] { $"/{moduleKey}" } });

        var disable = await client.DeleteAsync($"{Route}/{moduleKey}");
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);

        var listing = await client.GetFromJsonAsync<ModuleEndpoints.EnabledModulesResponse>(Route);
        Assert.DoesNotContain(listing!.Modules, m => m.ModuleKey == moduleKey);

        // Non-destructive: the tombstoned row is still in the table (the erasure job's own history read).
        await using var db = fixture.CreateDbContext();
        var row = await db.EnabledModules.AsNoTracking()
            .SingleAsync(m => m.SiteId == fixture.SeededSiteId && m.ModuleKey == new ModuleKey(moduleKey));
        Assert.NotNull(row.RevokedAt);
    }

    /// <summary>"Only ever affects the caller's own site": the same admin, holding `site:configure` on
    /// their own site, cannot enable a module for a tenant they do not administer.</summary>
    [Fact]
    public async Task DemoAdminToken_CannotEnableAModuleForAnotherTenant()
    {
        var victimSiteId = new SiteId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(victimSiteId, $"site_{victimSiteId.Value:N}", []));
            await db.SaveChangesAsync();
        }

        var moduleKey = UniqueModuleKey();
        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var response = await client.PutAsJsonAsync(
            $"/api/v1/sites/{victimSiteId.Value}/modules/{moduleKey}", new { triggerWords = new[] { $"/{moduleKey}" } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("Conversation.Forbidden", problem!.Title);
    }

    /// <summary>The route exists now, so an unauthenticated caller is refused at authentication (`401`),
    /// not routing - the mirror of the `404` rotate/verify still get for a route that does not exist.</summary>
    [Fact]
    public async Task NoToken_CannotEnableAModule()
    {
        var token = await fixture.GetDemoAdminAccessTokenAsync();
        _ = token;
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token: null);

        var response = await client.PutAsJsonAsync($"{Route}/{UniqueModuleKey()}", new { triggerWords = new[] { "/x" } });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>The platform-owner grant stays an override: a tenant cannot disable a module a platform
    /// owner granted (`GrantedByOwner`), so this returns `409` and leaves it enabled.</summary>
    [Fact]
    public async Task DemoAdminToken_CannotDisableAPlatformOwnerGrant()
    {
        var moduleKey = UniqueModuleKey();
        await using (var db = fixture.CreateDbContext())
        {
            db.EnabledModules.Add(new EnabledModule(
                new EnabledModuleId(Guid.NewGuid()), fixture.SeededSiteId, new ModuleKey(moduleKey), [$"/{moduleKey}"],
                new Uri("https://calendar.example.com"), new ModuleCredential("an-owner-granted-secret-of-sixteen-plus"),
                DateTimeOffset.UtcNow, grantedByOwner: true));
            await db.SaveChangesAsync();
        }

        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var response = await client.DeleteAsync($"{Route}/{moduleKey}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("Module.DisableOwnerGrantRefused", problem!.Title);
    }

    // ------------------------------------------------------------------------------------------
    // `26-320`: the tenant admin's own edit of an already-enabled module's trigger words, end-to-end.
    // ------------------------------------------------------------------------------------------

    /// <summary>The item's own headline: a tenant admin edits an enabled module's trigger words from their
    /// own settings, and the `GET` listing the console reads reflects the new set - no platform-owner action.</summary>
    [Fact]
    public async Task DemoAdminToken_CanEditTriggerWordsOfTheirOwnModule_ThenTheListingReflectsThem()
    {
        var moduleKey = UniqueModuleKey();
        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        await client.PutAsJsonAsync($"{Route}/{moduleKey}", new { triggerWords = new[] { $"/{moduleKey}" } });

        var edit = await client.PutAsJsonAsync(
            $"{Route}/{moduleKey}/trigger-words", new { triggerWords = new[] { $"/{moduleKey}", "/booking" } });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);

        var listing = await client.GetFromJsonAsync<ModuleEndpoints.EnabledModulesResponse>(Route);
        var enabled = Assert.Single(listing!.Modules, m => m.ModuleKey == moduleKey);
        Assert.Equal([$"/{moduleKey}", "/booking"], enabled.TriggerWords);
    }

    /// <summary>The platform-owner grant stays an override for trigger words too: a tenant cannot re-word a
    /// module a platform owner granted (`GrantedByOwner`), so this returns `409` and leaves the words as they
    /// were - the mirror of the disable refusal above, proving the new code's own status mapping.</summary>
    [Fact]
    public async Task DemoAdminToken_CannotEditTriggerWordsOfAPlatformOwnerGrant()
    {
        var moduleKey = UniqueModuleKey();
        await using (var db = fixture.CreateDbContext())
        {
            db.EnabledModules.Add(new EnabledModule(
                new EnabledModuleId(Guid.NewGuid()), fixture.SeededSiteId, new ModuleKey(moduleKey), [$"/{moduleKey}"],
                new Uri("https://calendar.example.com"), new ModuleCredential("an-owner-granted-secret-of-sixteen-plus"),
                DateTimeOffset.UtcNow, grantedByOwner: true));
            await db.SaveChangesAsync();
        }

        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var response = await client.PutAsJsonAsync(
            $"{Route}/{moduleKey}/trigger-words", new { triggerWords = new[] { "/booking" } });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("Module.TriggerWordsOwnerGrantRefused", problem!.Title);

        // The words are untouched.
        await using var db2 = fixture.CreateDbContext();
        var row = await db2.EnabledModules.AsNoTracking()
            .SingleAsync(m => m.SiteId == fixture.SeededSiteId && m.ModuleKey == new ModuleKey(moduleKey));
        Assert.Equal([$"/{moduleKey}"], row.TriggerWords);
    }

    // ------------------------------------------------------------------------------------------
    // `23-83`/`adr/0151`: rotate and verify stay gone from the tenant surface - a `404`, no route to
    // reach at all (the same "routing found nothing" distinction, unchanged by `26-316`).
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task AdminToken_CanNoLongerRotateACredential_TheRouteIsGone()
    {
        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var response = await client.PostAsJsonAsync(
            $"{Route}/faq/rotate", new { provisioningSecret = "a-provisioning-secret-of-sixteen-plus-chars" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AdminToken_CanNoLongerVerifyARegistration_TheRouteIsGone()
    {
        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var response = await client.PostAsJsonAsync(
            $"{Route}/faq/verify",
            new { entryPoint = "https://faq.example.com", provisioningSecret = "a-provisioning-secret-of-sixteen-plus-chars" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static string UniqueModuleKey() => $"ss-{Guid.NewGuid():N}";

    private static HttpClient CreateClient(WebApplication host, string? token)
    {
        var client = host.GetTestClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    private async Task<WebApplication> BuildTestHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddRouting();
        builder.Services.AddPlatformKernel();
        builder.Services.AddSingleton(fixture.DataSource);
        builder.Services.AddDbContext<AgoChatDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<Npgsql.NpgsqlDataSource>()));

        // The production registrations for this route, exactly as ChatModule/AddPostgresPersistence
        // make them. `26-316`: enable/disable are back on this group (rotate/verify stay owner-only).
        builder.Services.AddScoped<IOperatorRepository, OperatorRepository>();
        // `25-170`: ResolveOperatorIdentityHandler now composes IOperatorRoleRepository instead of
        // IPermissionChecker - CanSignIn is the one-rule "does any held role still hold its own seat" form.
        builder.Services.AddScoped<IOperatorRoleRepository, OperatorRoleRepository>();
        builder.Services.AddScoped<ResolveOperatorIdentityHandler>();
        builder.Services.AddScoped<IPermissionChecker, PermissionChecker>();
        builder.Services.AddScoped<IEnabledModuleRepository, EnabledModuleRepository>();
        builder.Services.AddScoped<IEnabledModuleReadStore, EnabledModuleReadStore>();
        builder.Services.AddScoped<ListEnabledModulesForSiteHandler>();

        // `26-316`: the self-serve enable/disable handlers and their ports. DB-backed collaborators are
        // the real production types (so the toggle really writes and clears rows); the external module
        // deployment and the deployment-configured secret/entry point/permission map are stubbed - the
        // module lives in another repository, and this test is about Chat's own side of the toggle.
        builder.Services.AddScoped<IRoleRepository, RoleRepository>();
        builder.Services.AddScoped<ISiteRepository, SiteRepository>();
        builder.Services.AddScoped<IModuleCredentialGenerator, ModuleCredentialGenerator>();
        builder.Services.AddSingleton<IModuleRegistrationGateway, StubModuleRegistrationGateway>();
        builder.Services.AddSingleton<IModuleProvisioningSecretProvider, StubModuleProvisioningSecretProvider>();
        builder.Services.AddSingleton<IModuleEntryPointProvider, StubModuleEntryPointProvider>();
        builder.Services.AddSingleton<IModulePermissionsProvider, StubModulePermissionsProvider>();
        builder.Services.AddScoped<Application.UseCases.EnableModuleForSite.EnableModuleForSiteHandler>();
        builder.Services.AddScoped<Application.UseCases.DisableModuleForSite.DisableModuleForSiteHandler>();
        // `26-320`: the tenant admin's own trigger-word edit - a Chat-side-only write (no module call), so
        // it needs no gateway or secret beyond the permission checker, repository, read store and clock
        // already registered above.
        builder.Services.AddScoped<Application.UseCases.SetModuleTriggerWordsForSite.SetModuleTriggerWordsForSiteHandler>();

        builder.Services.AddHttpContextAccessor();
        // `23-73`: OperatorIdentityClaimsTransformation's own new dependencies - the watchdog
        // reset hook and (where this host did not already have one) IClock.
        builder.Services.AddScoped<Ago.Chat.Application.Abstractions.ISiteActivityWatchdog, Ago.Chat.Infrastructure.Postgres.SiteActivityWatchdogRepository>();
        builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new Ago.Chat.Infrastructure.Postgres.SiteActivityWatchdogOptions()));
        builder.Services.AddSingleton<IClaimsTransformation, OperatorIdentityClaimsTransformation>();

        builder.Services.AddAuthentication()
            .AddJwtBearer(JwtSchemes.Operator, options =>
            {
                options.MapInboundClaims = false;
                options.Authority = fixture.KeycloakAuthority;
                options.RequireHttpsMetadata = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateAudience = true,
                    ValidAudience = OperatorOidcFixture.ClientId,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero,
                };
            });
        builder.Services.AddAuthorization(options =>
        {
            // `Program.cs`'s own declaration, reproduced verbatim.
            options.AddPolicy("RequireOperatorIdentity", policy => policy
                .AddAuthenticationSchemes(JwtSchemes.Operator)
                .RequireClaim(AgoClaimTypes.OperatorId));
        });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        // The real production mapping - no duplicated route or policy decision.
        app.MapModuleEndpoints();

        await app.StartAsync();
        return app;
    }

    /// <summary>`26-316`: the external module deployment's own half of a registration, stubbed to succeed -
    /// it lives in another repository (`adr/0065`), so an integration test of Chat's own toggle records
    /// the call and returns success rather than reaching a real module over HTTP.</summary>
    private sealed class StubModuleRegistrationGateway : IModuleRegistrationGateway
    {
        public Task RegisterAsync(
            ModuleRegistrationTarget module, ModuleCredential credential, ModuleProvisioningSecret provisioningSecret,
            string displayName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RotateAsync(
            ModuleRegistrationTarget module, ModuleCredential newCredential, ModuleProvisioningSecret provisioningSecret,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RevokeAsync(
            ModuleRegistrationTarget module, ModuleProvisioningSecret provisioningSecret, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<ModuleRegistrationRemoteStatus> GetStatusAsync(
            ModuleRegistrationTarget module, ModuleProvisioningSecret provisioningSecret, CancellationToken cancellationToken) =>
            Task.FromResult(new ModuleRegistrationRemoteStatus(Exists: true, DateTimeOffset.UtcNow, HasCredentialInGracePeriod: false));

        public Task<TenantDataErasureResult> EraseTenantDataAsync(
            ModuleRegistrationTarget module, ModuleProvisioningSecret provisioningSecret, CancellationToken cancellationToken) =>
            Task.FromResult(new TenantDataErasureResult(TenantExisted: false, Confirmed: true));

        public Task<ModuleTenantExportResult> ExportTenantDataAsync(
            ModuleRegistrationTarget module, ModuleProvisioningSecret provisioningSecret, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The self-serve toggle never exports tenant data.");
    }

    private sealed class StubModuleProvisioningSecretProvider : IModuleProvisioningSecretProvider
    {
        public ModuleProvisioningSecret? TryGet() => new("a-provisioning-secret-of-sixteen-plus-chars");
    }

    private sealed class StubModuleEntryPointProvider : IModuleEntryPointProvider
    {
        public Uri? TryGet(ModuleKey moduleKey) => new("https://calendar.example.com");
    }

    private sealed class StubModulePermissionsProvider : IModulePermissionsProvider
    {
        public ModulePermissionSet Get(ModuleKey moduleKey) => ModulePermissionSet.Empty;
    }
}
