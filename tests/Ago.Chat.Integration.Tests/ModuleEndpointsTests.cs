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
/// `19-03`/`22-11` built `PUT`/rotate/revoke/verify on `/api/v1/sites/{siteId}/modules` alongside the
/// `GET` this file also proves; `23-83`/`adr/0151` removed all four writes, not re-plumbed - a tenant
/// operator never turns a module on for themselves, only the platform (`OwnerModuleEndpointsTests`'
/// own route) or a payment the system has not built yet (`adr/0151`'s own "what this does not
/// decide"). This file now proves two things instead of five: the surviving read still works exactly
/// as `23-01` left it, and every removed route is actually gone for a tenant - not merely refused for
/// lacking a permission, refused because there is no route left to reach at all.
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
    // `23-83`/`adr/0151`: the fails-before proof for a removal. Before this item, each of these four
    // calls reached a handler (and, for `PUT`, actually registered a module - the "must not become the
    // normal path" claim this file used to name for the boundary between this route and the owner's
    // own). After it, there is no route to dispatch to at all: MapGroup's `RequireAuthorization`
    // middleware never runs, because ASP.NET Core's own endpoint-routing middleware has nothing
    // matching this verb+path pair to hand to it - the same reason `NoToken_CannotListModules` above
    // gets a `401` (a route was found, authentication was not) while every test below gets a `404` (no
    // route was found at all, so authentication was never asked about). That distinction is the whole
    // proof that this is a removed route, not merely a route that now always refuses.
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// `23-83`: <b>405, not 404, and the difference is the point.</b> The collection path itself
    /// survives - `GET` on it stays, because a tenant seeing which products are on their account is
    /// ordinary and carries no secret. So what was removed is the <i>method</i>, not the resource, and
    /// ASP.NET answers a known path with an unmapped verb the way HTTP says to. Asserting 404 here
    /// would have been asserting that the read had gone too.
    /// </summary>
    [Fact]
    public async Task AdminToken_CanNoLongerEnableAModule_TheWriteMethodIsGone()
    {
        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var response = await client.PutAsJsonAsync(
            Route, new
            {
                moduleKey = "faq",
                triggerWords = new[] { "/faq" },
                entryPoint = "https://faq.example.com",
                credential = "a-shared-secret-of-sixteen-plus-chars",
                provisioningSecret = "a-provisioning-secret-of-sixteen-plus-chars",
            });

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

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
    public async Task AdminToken_CanNoLongerRevokeAModule_TheRouteIsGone()
    {
        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{Route}/faq")
        {
            Content = JsonContent.Create(new { provisioningSecret = "a-provisioning-secret-of-sixteen-plus-chars" }),
        };
        var response = await client.SendAsync(request);

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

    /// <summary>The removal is not merely "this token cannot" - nobody can, because there is nothing
    /// to authenticate for. Routing runs before authentication in the ASP.NET Core pipeline, so an
    /// unauthenticated caller gets the identical `404` an authenticated one does above.</summary>
    [Fact]
    public async Task NoToken_IsRefusedTheSameWay_OnTheRemovedEnableWrite()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token: null);

        var response = await client.PutAsJsonAsync(
            Route, new
            {
                moduleKey = "faq",
                triggerWords = new[] { "/faq" },
                entryPoint = "https://faq.example.com",
                credential = "a-shared-secret-of-sixteen-plus-chars",
            });

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

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
        // make them. `23-83`: only the GET route's own handler is left in this group - see
        // `ModuleEndpoints`'s own remarks for why `EnableModuleForSiteHandler`/
        // `RotateModuleCredentialHandler`/`RevokeModuleForSiteHandler`/`VerifyModuleRegistrationHandler`
        // are not registered here any more; they no longer exist.
        builder.Services.AddScoped<IOperatorRepository, OperatorRepository>();
        builder.Services.AddScoped<ResolveOperatorIdentityHandler>();
        builder.Services.AddScoped<IPermissionChecker, PermissionChecker>();
        builder.Services.AddScoped<IEnabledModuleRepository, EnabledModuleRepository>();
        builder.Services.AddScoped<IEnabledModuleReadStore, EnabledModuleReadStore>();
        builder.Services.AddScoped<ListEnabledModulesForSiteHandler>();

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
}
