using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Owner;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Application.UseCases.UpdateSiteAllowedOriginsAsOwner;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
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
/// `23-48`'s own Done-when, against a real Keycloak and a real Postgres (<see cref="OperatorOidcFixture"/>):
/// `PUT /api/v1/owner/sites/{siteId}/allowed-origins` - who may call it, that a malformed origin is
/// refused naming what is wrong with it, and that an empty list is refused rather than locking the
/// widget out of every page. The cache-invalidation half of this item's own brief
/// ("the change takes effect immediately, proven against the cache rather than assumed") is proven
/// separately, end to end against real Postgres/RabbitMQ/Redis, in
/// <see cref="SiteAllowedOriginsCacheInvalidationEndToEndTests"/> - this file is the HTTP surface and
/// the authorization boundary only.
///
/// <para>Runs the production <c>OwnerSiteAllowedOriginsEndpoints.MapOwnerSiteAllowedOriginsEndpoint</c>/
/// <c>UpdateSiteAllowedOriginsAsOwnerHandler</c> against a minimal <see cref="TestServer"/>, the same
/// seam <see cref="OwnerSiteDetailEndpointTests"/>/<see cref="OwnerModuleEndpointsTests"/> already
/// established.</para>
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class OwnerSiteAllowedOriginsEndpointTests(OperatorOidcFixture fixture)
{
    private static string Route(Guid siteId) => $"/api/v1/owner/sites/{siteId}/allowed-origins";

    [Fact]
    public async Task OwnerToken_WithValidOrigins_ReplacesTheSitesAllowedOrigins_AndEchoesTheSavedList()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await SeedBareTenantAsync(siteId, ["https://old.example"]);

        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await client.PutAsJsonAsync(
            Route(siteId.Value),
            new OwnerSiteAllowedOriginsEndpoints.UpdateAllowedOriginsRequest(["https://new.example"]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OwnerSiteAllowedOriginsEndpoints.UpdateAllowedOriginsResponse>();
        Assert.NotNull(body);
        Assert.Equal(["https://new.example"], body.AllowedOrigins);

        await using var db = fixture.CreateDbContext();
        var saved = await new SiteRepository(db).GetByIdAsync(siteId, CancellationToken.None);
        Assert.Equal(["https://new.example"], saved!.AllowedOrigins);
    }

    /// <summary>Fails-before: before this handler validated each entry, a value carrying a path would
    /// have been stored verbatim and then never matched a real browser's `Origin` header - the widget
    /// would silently never connect (`23-48`'s own "Why it costs more than it looks"). The message
    /// names what is wrong, matching this item's own Done-when.</summary>
    [Fact]
    public async Task OwnerToken_WithAMalformedOrigin_Returns400_NamingWhatIsWrong()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await SeedBareTenantAsync(siteId, ["https://old.example"]);

        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await client.PutAsJsonAsync(
            Route(siteId.Value),
            new OwnerSiteAllowedOriginsEndpoints.UpdateAllowedOriginsRequest(["https://shop.example/booking"]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadAsStringAsync();
        Assert.Contains("path", problem, StringComparison.OrdinalIgnoreCase);

        await using var db = fixture.CreateDbContext();
        var saved = await new SiteRepository(db).GetByIdAsync(siteId, CancellationToken.None);
        Assert.Equal(["https://old.example"], saved!.AllowedOrigins);
    }

    /// <summary>Fails-before: before this guard existed, an empty list would have been saved verbatim
    /// - locking every visitor out of a widget that otherwise still looks fully configured.</summary>
    [Fact]
    public async Task OwnerToken_WithAnEmptyList_Returns400_AndSavesNothing()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await SeedBareTenantAsync(siteId, ["https://old.example"]);

        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await client.PutAsJsonAsync(
            Route(siteId.Value), new OwnerSiteAllowedOriginsEndpoints.UpdateAllowedOriginsRequest([]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var db = fixture.CreateDbContext();
        var saved = await new SiteRepository(db).GetByIdAsync(siteId, CancellationToken.None);
        Assert.Equal(["https://old.example"], saved!.AllowedOrigins);
    }

    [Fact]
    public async Task OwnerToken_ForANonexistentSite_Returns404()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await client.PutAsJsonAsync(
            Route(Guid.NewGuid()),
            new OwnerSiteAllowedOriginsEndpoints.UpdateAllowedOriginsRequest(["https://new.example"]));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ------------------------------------------------------------------------------------------
    // The authorization boundary: this route is RequirePlatformOwner and nothing weaker - the
    // author's own decision that nobody but the platform owner may ever call this, not even the
    // tenant whose site it is (`23-48`'s "the answer").
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task OrdinaryOperatorToken_IsRejected_AndNothingIsWritten()
    {
        var siteId = fixture.SeededSiteId;
        await using var db = fixture.CreateDbContext();
        var before = await new SiteRepository(db).GetByIdAsync(siteId, CancellationToken.None);
        var originsBefore = before!.AllowedOrigins;

        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, await fixture.GetDemoOperatorAccessTokenAsync());

        var response = await client.PutAsJsonAsync(
            Route(siteId.Value),
            new OwnerSiteAllowedOriginsEndpoints.UpdateAllowedOriginsRequest(["https://attacker.example"]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // `23-43`'s own concern, restated for a write rather than a read: the refusal carries no
        // body at all - not "you may not touch this site" (which would at least confirm the site
        // exists), nothing. `RequireAuthorization`'s own middleware refuses before routing ever
        // resolves the handler, so there is no code path here that could tell a refused caller
        // anything about the named site one way or the other.
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());

        await using var afterDb = fixture.CreateDbContext();
        var after = await new SiteRepository(afterDb).GetByIdAsync(siteId, CancellationToken.None);
        Assert.Equal(originsBefore, after!.AllowedOrigins);
    }

    /// <summary>`23-43`'s own concern, checked mechanically for this write rather than assumed from
    /// the policy's shape: a refused caller cannot tell "this site does not exist" apart from "this
    /// site exists, and you may not touch it". Both a real, seeded site id and a freshly minted one
    /// that names nothing at all get the identical 403 with an identical empty body - the
    /// `Site.NotFound` check inside the handler never runs for either, because
    /// `RequireAuthorization("RequirePlatformOwner")` refuses the request before the handler is ever
    /// reached (the same reasoning `OwnerToken_ForANonexistentSite_Returns404`'s own 404 depends on:
    /// that branch is real precisely because it is the platform owner's own token reaching it).</summary>
    [Fact]
    public async Task OrdinaryOperatorToken_GetsTheIdenticalRefusal_WhetherTheNamedSiteExistsOrNot()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, await fixture.GetDemoOperatorAccessTokenAsync());
        var request = new OwnerSiteAllowedOriginsEndpoints.UpdateAllowedOriginsRequest(["https://attacker.example"]);

        var forRealSite = await client.PutAsJsonAsync(Route(fixture.SeededSiteId.Value), request);
        var forMissingSite = await client.PutAsJsonAsync(Route(Guid.NewGuid()), request);

        Assert.Equal(HttpStatusCode.Forbidden, forRealSite.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forMissingSite.StatusCode);
        Assert.Empty(await forRealSite.Content.ReadAsByteArrayAsync());
        Assert.Empty(await forMissingSite.Content.ReadAsByteArrayAsync());
    }

    /// <summary>The case that matters most, the identical lesson `OwnerModuleEndpointsTests` and
    /// `OwnerSiteDetailEndpointTests` already prove: `5-08`'s site-wide `"Admin"`, holding
    /// `site:configure` for their own site, is still not the platform owner - a permission granted
    /// broadly inside one tenant does not become a cross-tenant write, even for the tenant's own
    /// site.</summary>
    [Fact]
    public async Task SiteConfigureHoldingAdminToken_IsRejected_EvenForTheirOwnSite()
    {
        var siteId = fixture.SeededSiteId;

        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, await fixture.GetDemoAdminAccessTokenAsync());

        var response = await client.PutAsJsonAsync(
            Route(siteId.Value),
            new OwnerSiteAllowedOriginsEndpoints.UpdateAllowedOriginsRequest(["https://attacker.example"]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NoToken_IsRejected()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token: null);

        var response = await client.PutAsJsonAsync(
            Route(fixture.SeededSiteId.Value),
            new OwnerSiteAllowedOriginsEndpoints.UpdateAllowedOriginsRequest(["https://attacker.example"]));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    private async Task SeedBareTenantAsync(SiteId siteId, IReadOnlyList<string> allowedOrigins)
    {
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", allowedOrigins, "Allowed-Origins Test Tenant"));
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
        builder.Services.AddPlatformKernel();
        builder.Services.AddSingleton(fixture.DataSource);
        builder.Services.AddDbContext<AgoChatDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<Npgsql.NpgsqlDataSource>()));

        builder.Services.AddScoped<IOperatorRepository, OperatorRepository>();
        // `23-71`: ResolveOperatorIdentityHandler now composes IPermissionChecker - see
        // OfflineAutoReplyDeliveryEndToEndTests' own remarks on this same addition.
        builder.Services.AddScoped<IPermissionChecker, PermissionChecker>();
        builder.Services.AddScoped<ResolveOperatorIdentityHandler>();
        builder.Services.AddScoped<ISiteRepository, SiteRepository>();
        builder.Services.AddScoped<IOutboxWriter, EfOutboxWriter<AgoChatDbContext>>();
        builder.Services.AddSingleton<IClock, Ago.Platform.Hosting.SystemClock>();
        builder.Services.AddScoped<UpdateSiteAllowedOriginsAsOwnerHandler>();

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

        app.MapOwnerSiteAllowedOriginsEndpoint();

        await app.StartAsync();
        return app;
    }
}
