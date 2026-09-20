using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Owner;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetOwnerSeatSummary;
using Ago.Chat.Application.UseCases.GrantOwnerSeatsAsOwner;
using Ago.Chat.Application.UseCases.OperatorRoleSeats;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Hosting;
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
/// `25-181`'s own Done-when, over a real HTTP pipeline and a real Postgres: the platform owner can
/// grant a tenant extra seats by hand, the raised limit is visible through the summary route
/// immediately, the write is recorded with the real owner subject, and nobody but the platform owner
/// can reach either route. A deliberate sibling to <see cref="OwnerOperatorsEndpointsTests"/>'s own
/// test host, not a shared one - see <see cref="OwnerOperatorsEndpoints"/>'s own remarks for why:
/// each endpoint file's test host registers only what its own routes need.
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class OwnerSeatGrantsEndpointsTests(OperatorOidcFixture fixture)
{
    private static string SeatSummaryRoute(Guid siteId) => $"/api/v1/owner/sites/{siteId}/seat-summary";

    private static string SeatGrantsRoute(Guid siteId) => $"/api/v1/owner/sites/{siteId}/seat-grants";

    [Fact]
    public async Task OwnerToken_GrantsExtraAdministratorSeats_AndTheSummaryReflectsItImmediately()
    {
        var siteId = await SeedSiteAsync();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var grantResponse = await ownerClient.PostAsJsonAsync(
            SeatGrantsRoute(siteId.Value),
            new OwnerSeatGrantsEndpoints.GrantOwnerSeatsRequest("Administrator", 2, "Incident cover.", null));
        Assert.Equal(HttpStatusCode.OK, grantResponse.StatusCode);

        var summaryResponse = await ownerClient.GetAsync(SeatSummaryRoute(siteId.Value));
        Assert.Equal(HttpStatusCode.OK, summaryResponse.StatusCode);
        var summary = await summaryResponse.Content.ReadFromJsonAsync<OwnerSeatSummaryDto>();
        Assert.NotNull(summary);
        // SubscriptionTierBands.FreeAdminsIncluded (1) + the 2 just granted.
        Assert.Equal(3, summary.AdministratorsLimit);
    }

    [Fact]
    public async Task OwnerToken_GrantsExtraOperatorSeats_RaisesTheOperatorLimit_NotTheAdministratorOne()
    {
        var siteId = await SeedSiteAsync(seatLimit: 2);
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var grantResponse = await ownerClient.PostAsJsonAsync(
            SeatGrantsRoute(siteId.Value),
            new OwnerSeatGrantsEndpoints.GrantOwnerSeatsRequest("Operator", 3, "Extra seats for a seasonal push.", null));
        Assert.Equal(HttpStatusCode.OK, grantResponse.StatusCode);

        var summary = await (await ownerClient.GetAsync(SeatSummaryRoute(siteId.Value)))
            .Content.ReadFromJsonAsync<OwnerSeatSummaryDto>();
        Assert.NotNull(summary);
        Assert.Equal(5, summary.OperatorsLimit);
        Assert.Equal(1, summary.AdministratorsLimit); // untouched
    }

    [Fact]
    public async Task OwnerToken_ABlankReason_IsRefused_400()
    {
        var siteId = await SeedSiteAsync();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await ownerClient.PostAsJsonAsync(
            SeatGrantsRoute(siteId.Value),
            new OwnerSeatGrantsEndpoints.GrantOwnerSeatsRequest("Administrator", 1, "   ", null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task OwnerToken_AQuantityOutsideOneToFive_IsRefused_400(int quantity)
    {
        var siteId = await SeedSiteAsync();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await ownerClient.PostAsJsonAsync(
            SeatGrantsRoute(siteId.Value),
            new OwnerSeatGrantsEndpoints.GrantOwnerSeatsRequest("Administrator", quantity, "A reason.", null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OwnerToken_AnUnrecognisedRole_IsRefused_400()
    {
        var siteId = await SeedSiteAsync();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await ownerClient.PostAsJsonAsync(
            SeatGrantsRoute(siteId.Value),
            new OwnerSeatGrantsEndpoints.GrantOwnerSeatsRequest("SuperAdmin", 1, "A reason.", null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OwnerToken_GrantingForANonexistentSite_Returns404()
    {
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await ownerClient.PostAsJsonAsync(
            SeatGrantsRoute(Guid.NewGuid()),
            new OwnerSeatGrantsEndpoints.GrantOwnerSeatsRequest("Administrator", 1, "A reason.", null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task OwnerToken_ReadingTheSummaryForANonexistentSite_Returns404()
    {
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await ownerClient.GetAsync(SeatSummaryRoute(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>`24-12`: the access record, proven with the real owner subject off the real token - the
    /// identical proof `OwnerOperatorsEndpointsTests`'s own equivalent test gives for its own write.
    /// </summary>
    [Fact]
    public async Task OwnerToken_GrantingSeats_LeavesAnAccessRecord_NamingTheRealOwnerSubject()
    {
        var siteId = await SeedSiteAsync();
        var token = await fixture.GetPlatformOwnerAccessTokenAsync();
        var ownerSubject = new JwtSecurityTokenHandler().ReadJwtToken(token).Subject;
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, token);

        var response = await ownerClient.PostAsJsonAsync(
            SeatGrantsRoute(siteId.Value),
            new OwnerSeatGrantsEndpoints.GrantOwnerSeatsRequest("Administrator", 1, "A recorded reason.", null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var records = await new AccessRecordRepository(fixture.DataSource)
            .ListForSiteAsync(siteId, beforeId: null, limit: 50, CancellationToken.None);
        var recorded = Assert.Single(records.Items, r => r.AccessKind == AccessRecordKind.OwnerSeatGrant);
        Assert.Equal(AccessRecordActorKind.PlatformOwner, recorded.ActorKind);
        Assert.Equal(ownerSubject, recorded.ActorId);
        Assert.Equal(AccessRecordResourceKind.OwnerSeatGrant, recorded.ResourceKind);
        Assert.Null(recorded.ResourceId);
    }

    // ------------------------------------------------------------------------------------------
    // The authorization boundary: both routes are RequirePlatformOwner and nothing weaker.
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task OrdinaryOperatorToken_IsRejected()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, await fixture.GetDemoOperatorAccessTokenAsync());

        var response = await client.GetAsync(SeatSummaryRoute(fixture.SeededSiteId.Value));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>`5-08`'s site-wide `"Admin"`, holding `site:manage_operators` for their own site, is
    /// still not the platform owner - the identical lesson `OwnerOperatorsEndpointsTests` already
    /// proves for its own owner surface.</summary>
    [Fact]
    public async Task SiteManageOperatorsHoldingAdminToken_IsRejected()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, await fixture.GetDemoAdminAccessTokenAsync());

        var response = await client.PostAsJsonAsync(
            SeatGrantsRoute(fixture.SeededSiteId.Value),
            new OwnerSeatGrantsEndpoints.GrantOwnerSeatsRequest("Administrator", 1, "A reason.", null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NoToken_IsRejected()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token: null);

        var response = await client.GetAsync(SeatSummaryRoute(fixture.SeededSiteId.Value));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<SiteId> SeedSiteAsync(int seatLimit = 5)
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [], seatLimit: seatLimit));
        await db.SaveChangesAsync();
        return siteId;
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
        builder.Services.AddScoped<IOperatorRoleRepository, OperatorRoleRepository>();
        builder.Services.AddScoped<ResolveOperatorIdentityHandler>();
        builder.Services.AddScoped<IPermissionChecker, PermissionChecker>();
        builder.Services.AddScoped<ISiteRepository, SiteRepository>();
        builder.Services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        builder.Services.AddScoped<OperatorRoleSeatCapacity>();
        builder.Services.AddScoped<IOwnerSeatGrantStore, OwnerSeatGrantStore>();
        builder.Services.AddScoped<GetOwnerSeatSummaryHandler>();
        builder.Services.AddScoped<GrantOwnerSeatsAsOwnerHandler>();
        // `24-12`: the owner endpoint's own access-record write - OwnerAccessRecorder resolves this
        // straight from DI, the same way the production host does. IClock/IIdGenerator are already
        // registered above (AddPlatformKernel).
        builder.Services.AddScoped<IAccessRecordRepository, AccessRecordRepository>();
        builder.Services.AddSingleton<IClock, Ago.Platform.Hosting.SystemClock>();

        builder.Services.AddHttpContextAccessor();
        // `23-73`: OperatorIdentityClaimsTransformation's own new dependencies - unused by either route
        // this host maps, but ResolveOperatorIdentityHandler's own registration above needs the type to
        // resolve, the identical reason OwnerOperatorsEndpointsTests' own host registers it.
        builder.Services.AddScoped<ISiteActivityWatchdog, SiteActivityWatchdogRepository>();
        builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new SiteActivityWatchdogOptions()));
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

        app.MapOwnerSeatGrantsEndpoints();

        await app.StartAsync();
        return app;
    }
}
