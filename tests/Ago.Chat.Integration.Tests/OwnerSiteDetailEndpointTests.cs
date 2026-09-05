using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Owner;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetSiteForOwner;
using Ago.Chat.Application.UseCases.ListSitesForOwner;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-14`'s own Done-when, against a real Keycloak and a real Postgres
/// (<see cref="OperatorOidcFixture"/>): `GET /api/v1/owner/sites/{siteId}`, the platform owner's
/// per-tenant detail read - who may call it, what it returns for a tenant with real entitlements, and
/// what it does for one that does not exist.
///
/// <para>Runs the production <c>OwnerSitesEndpoints.MapOwnerEndpoints</c>/
/// <c>GetSiteForOwnerHandler</c>/<c>PlatformOverviewReadStore</c>/<c>EnabledModuleReadStore</c> against
/// a minimal <see cref="TestServer"/>, the same seam <see cref="OwnerSitesEndpointTests"/> already
/// established for this route file - deliberately a separate test class rather than more methods on
/// that one, since this route's own failure mode (a named site that does not exist) and its own
/// distinguishing claim (entitlements, not just the eight aggregate facts) are a different story from
/// the list's search and pagination.</para>
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class OwnerSiteDetailEndpointTests(OperatorOidcFixture fixture)
{
    private const string RouteBase = "/api/v1/owner/sites";

    [Fact]
    public async Task OwnerToken_GetsTheSiteDetail_WithGroundTruthNumbers()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var createdAt = DateTimeOffset.UtcNow.AddDays(-7);
        await SeedBareTenantAsync(siteId, "Detail Read Tenant", createdAt);

        var token = await fixture.GetPlatformOwnerAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var response = await client.GetAsync($"{RouteBase}/{siteId.Value}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OwnerSiteDetailResponse>();
        Assert.NotNull(body);
        Assert.Equal(siteId.Value, body.SiteId);
        Assert.Equal("Detail Read Tenant", body.Name);
        Assert.Equal("free", body.Tier);
        Assert.NotNull(body.CreatedAt);
        Assert.True((body.CreatedAt.Value - createdAt).Duration() < TimeSpan.FromMilliseconds(1));
        Assert.Equal(ListSitesForOwnerHandler.RecentWindowDays, body.RecentWindowDays);
        Assert.Empty(body.Modules);
    }

    /// <summary>The detail read's own reason to exist beyond the list: a module the platform owner
    /// granted, with its expiry, distinguishable from one the tenant enabled themselves.</summary>
    [Fact]
    public async Task OwnerToken_SeesAModuleTheOwnerGranted_DistinguishableFromATenantGrantedOne_WithExpiry()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await SeedBareTenantAsync(siteId, "Entitlements Tenant", DateTimeOffset.UtcNow);

        var now = DateTimeOffset.UtcNow;
        var ownerExpiry = now.AddDays(30);
        await SeedModuleAsync(siteId, "calendar", grantedByOwner: true, enabledAt: now, expiresAt: ownerExpiry);
        await SeedModuleAsync(siteId, "faq", grantedByOwner: false, enabledAt: now, expiresAt: null);

        var token = await fixture.GetPlatformOwnerAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var body = await GetDetailAsync(client, siteId.Value);

        var ownerGranted = Assert.Single(body.Modules, m => m.ModuleKey == "calendar");
        Assert.True(ownerGranted.GrantedByOwner);
        Assert.NotNull(ownerGranted.ExpiresAt);
        Assert.True((ownerGranted.ExpiresAt.Value - ownerExpiry).Duration() < TimeSpan.FromMilliseconds(1));
        Assert.True(ownerGranted.IsActive);

        var tenantGranted = Assert.Single(body.Modules, m => m.ModuleKey == "faq");
        Assert.False(tenantGranted.GrantedByOwner);
        // A grant with no expiry renders as an explicit "no end date" on the console
        // (OwnerSiteDetailPage) - at the wire level that is simply a null the console must not treat
        // as a blank cell, which is this assertion's job to guard.
        Assert.Null(tenantGranted.ExpiresAt);
        Assert.True(tenantGranted.IsActive);
    }

    /// <summary>The Done-when the item's author was most explicit about: an expired grant is shown as
    /// expired, using the live read-store's own `expires_at > now` decision - not omitted, and not
    /// left for the console to work out by comparing dates itself.</summary>
    [Fact]
    public async Task OwnerToken_AnExpiredGrant_IsShownAsExpired_NotOmitted()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await SeedBareTenantAsync(siteId, "Lapsed Trial Tenant", DateTimeOffset.UtcNow);

        var enabledAt = DateTimeOffset.UtcNow.AddDays(-30);
        var expiresAt = DateTimeOffset.UtcNow.AddDays(-5);
        await SeedModuleAsync(siteId, "calendar", grantedByOwner: true, enabledAt: enabledAt, expiresAt: expiresAt);

        var token = await fixture.GetPlatformOwnerAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var body = await GetDetailAsync(client, siteId.Value);

        // Still present - the whole point of this read being a diagnostic history rather than
        // `23-01`'s "currently active only" listing (EnabledModuleDetailSummary's own remarks).
        var expired = Assert.Single(body.Modules, m => m.ModuleKey == "calendar");
        Assert.NotNull(expired.ExpiresAt);
        Assert.False(expired.IsActive);
    }

    [Fact]
    public async Task OwnerToken_ForANonexistentSite_Returns404()
    {
        var token = await fixture.GetPlatformOwnerAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var response = await client.GetAsync($"{RouteBase}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>`23-14`'s own Done-when: a non-owner gets the same refusal the list gives, and no site
    /// data is loaded - proven against a real seeded tenant's own id, so a 403 cannot be mistaken for a
    /// 404 that would have happened anyway.</summary>
    [Fact]
    public async Task OrdinaryOperatorToken_IsRejected_AndNoSiteDataIsLoaded()
    {
        var token = await fixture.GetDemoOperatorAccessTokenAsync();

        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var response = await client.GetAsync($"{RouteBase}/{fixture.SeededSiteId.Value}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>The same site-wide `"Admin"`-is-not-the-platform-owner case
    /// <see cref="OwnerSitesEndpointTests.SiteConfigureHoldingAdminToken_IsRejected"/> proves for the
    /// list - restated here because this route's own access-control story
    /// (<see cref="GetSiteForOwnerHandler"/>'s remarks) is a second, independent claim, not a
    /// consequence of the list's.</summary>
    [Fact]
    public async Task SiteConfigureHoldingAdminToken_IsRejected()
    {
        var token = await fixture.GetDemoAdminAccessTokenAsync();

        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var response = await client.GetAsync($"{RouteBase}/{fixture.SeededSiteId.Value}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NoToken_IsRejected()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token: null);

        var response = await client.GetAsync($"{RouteBase}/{fixture.SeededSiteId.Value}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<OwnerSiteDetailResponse> GetDetailAsync(HttpClient client, Guid siteId)
    {
        var response = await client.GetAsync($"{RouteBase}/{siteId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OwnerSiteDetailResponse>();
        Assert.NotNull(body);
        return body;
    }

    private async Task SeedBareTenantAsync(SiteId siteId, string name, DateTimeOffset createdAt)
    {
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [], name, createdAt));
        await db.SaveChangesAsync();
    }

    private async Task SeedModuleAsync(
        SiteId siteId, string moduleKey, bool grantedByOwner, DateTimeOffset enabledAt, DateTimeOffset? expiresAt)
    {
        await using var db = fixture.CreateDbContext();
        db.EnabledModules.Add(new EnabledModule(
            new EnabledModuleId(Guid.NewGuid()),
            siteId,
            new ModuleKey(moduleKey),
            ["book-a-table"],
            new Uri("https://module.example.com/entry"),
            new ModuleCredential("a-sixteen-plus-character-secret"),
            enabledAt,
            grantedByOwner,
            expiresAt));
        await db.SaveChangesAsync();
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
        builder.Services.AddSingleton(fixture.DataSource);
        builder.Services.AddDbContext<AgoChatDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<Npgsql.NpgsqlDataSource>()));
        builder.Services.AddScoped<IOperatorRepository, OperatorRepository>();
        builder.Services.AddScoped<ResolveOperatorIdentityHandler>();
        // The production registrations for this route - exactly as ChatModule/AddPostgresPersistence
        // make them, the same shape OwnerSitesEndpointTests' own host-builder uses.
        builder.Services.AddScoped<IPlatformOverviewReadStore, PlatformOverviewReadStore>();
        builder.Services.AddScoped<IEnabledModuleReadStore, EnabledModuleReadStore>();
        builder.Services.AddScoped<ListSitesForOwnerHandler>();
        builder.Services.AddScoped<GetSiteForOwnerHandler>();
        builder.Services.AddSingleton<IClock, Ago.Platform.Hosting.SystemClock>();
        // `24-12`: the owner endpoint's own access-record write - OwnerAccessRecorder resolves these
        // straight from DI, the same way the production host does.
        builder.Services.AddScoped<IAccessRecordRepository, AccessRecordRepository>();
        builder.Services.AddSingleton<IIdGenerator, UuidV7Generator>();
        builder.Services.AddHttpContextAccessor();
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
            options.AddPolicy("RequirePlatformOwner", policy => policy
                .AddAuthenticationSchemes(JwtSchemes.Operator)
                .RequireAuthenticatedUser()
                .AddRequirements(new PlatformOwnerRequirement()));
        });
        builder.Services.AddSingleton<IAuthorizationHandler, PlatformOwnerAuthorizationHandler>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        // Only the detail route this file exercises - not MapOwnerEndpoints() (the list), which this
        // host has no registrations for. See OwnerSitesEndpoints' own class remarks for why the two
        // are separate Map calls.
        app.MapOwnerSiteDetailEndpoint();

        await app.StartAsync();
        return app;
    }
}
