using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Sites;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.RegisterSite;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `10-02`'s own Done-when, against a real Keycloak and a real Postgres
/// (<see cref="OperatorOidcFixture"/>): a real registration produces real, queryable rows; the
/// created operator's token subsequently works through `RequireOperatorIdentity`; a second call from
/// the same identity is rejected `409` with a real second call, not asserted from the handler's logic
/// alone. Every test mints its own fresh Keycloak user
/// (<see cref="OperatorOidcFixture.CreateFreshUserAccessTokenAsync"/>) rather than sharing one, since
/// registration is inherently a one-time state transition for a given identity.
///
/// Uses the real `Ago.Chat.Api.Sites.SitesEndpoints`/`RegisterSiteHandler`/
/// `SiteRegistrationRepository` production code against a minimal <see cref="TestServer"/> host, the
/// same seam <see cref="OperatorOidcAuthenticationTests"/> already established - `IRateLimiter` is the
/// in-repo always-allow <see cref="FakeRateLimiter"/> here deliberately: this file is about the
/// registration transaction and the identity round-trip, not rate limiting, which
/// `Ago.Chat.Concurrency.Tests.RegisterSiteRateLimitingConcurrencyTests` covers with a real Redis
/// bucket under real concurrency instead.
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class SiteRegistrationTests(OperatorOidcFixture fixture)
{
    [Fact]
    public async Task RegisterSite_WithARealKeycloakToken_CreatesOneSiteBothRolesOneOperatorAndBothOperatorRoles()
    {
        var (token, _) = await fixture.CreateFreshUserAccessTokenAsync();
        var externalSubjectId = ReadSubjectClaim(token);

        await using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync(
            "/api/v1/sites", new SitesEndpoints.RegisterSiteRequest("Acme Support", "https://shop.example.com"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SitesEndpoints.RegisterSiteResponse>();
        Assert.NotNull(body);
        Assert.Equal($"/api/v1/sites/{body.SiteId}", response.Headers.Location?.OriginalString);

        // Queried directly, not just asserted from the 201 - `10-02`'s own Done-when.
        await using var db = fixture.CreateDbContext();
        var site = await db.Sites.SingleAsync(s => s.Id == new SiteId(body.SiteId));
        Assert.Equal("Acme Support", site.Name);
        Assert.Contains("https://shop.example.com", site.AllowedOrigins);

        var operatorRow = await db.Operators.SingleAsync(o => o.Id == new OperatorId(body.OperatorId));
        Assert.Equal(site.Id, operatorRow.SiteId);
        Assert.Equal(externalSubjectId, operatorRow.ExternalSubjectId);

        var roles = await db.Roles.Where(r => r.SiteId == site.Id).ToListAsync();
        Assert.Equal(2, roles.Count);
        var operatorRole = Assert.Single(roles, r => r.Name == "Operator");
        Assert.Equal(
            [Permission.ConversationRead.Value, Permission.ConversationSend.Value, Permission.ConversationAssign.Value],
            operatorRole.Permissions);
        var adminRole = Assert.Single(roles, r => r.Name == "Admin");
        Assert.Equal(
            [Permission.SiteConfigure.Value, Permission.SiteManageOperators.Value, Permission.AttachmentDelete.Value],
            adminRole.Permissions);

        var operatorRoleIds = await db.OperatorRoles
            .Where(or => or.OperatorId == operatorRow.Id)
            .Select(or => or.RoleId)
            .ToListAsync();
        Assert.Equal(2, operatorRoleIds.Count);
        Assert.Contains(operatorRole.Id, operatorRoleIds);
        Assert.Contains(adminRole.Id, operatorRoleIds);
    }

    [Fact]
    public async Task RegisterSite_TheCreatedOperatorsToken_SubsequentlyWorksThroughRequireOperatorIdentity()
    {
        var (registrationToken, username) = await fixture.CreateFreshUserAccessTokenAsync();

        await using var host = await BuildTestHostAsync();
        using (var client = host.GetTestClient())
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registrationToken);
            var response = await client.PostAsJsonAsync(
                "/api/v1/sites", new SitesEndpoints.RegisterSiteRequest("Acme Support", "https://shop.example.com"));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        // A *second* token for the same identity - OperatorIdentityClaimsTransformation resolves
        // OperatorId/SiteId at request time from whatever `operators` row now matches `sub`, so a
        // fresh token (not the one used to register) proves the resolution is real, not an artifact
        // of some claim the registration call itself happened to carry.
        var operatorToken = await fixture.RefreshAccessTokenAsync(username);

        using var operatorClient = host.GetTestClient();
        operatorClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", operatorToken);
        var operatorOnlyResponse = await operatorClient.GetAsync("/operator-only");

        Assert.Equal(HttpStatusCode.OK, operatorOnlyResponse.StatusCode);
    }

    /// <summary>
    /// `13-07`/`adr/0068`'s own Done-when, replacing what used to be
    /// "...Returns409_NotASecondSite": before this item, a second `POST /api/v1/sites` from the same
    /// identity was refused `409` (the "one login, one tenant" constraint `10-02` deliberately shipped
    /// and this item deliberately relaxes). Now it succeeds, and produces a real, independently
    /// queryable second `Site`/`Operator` pair - `external_subject_id` equal, `site_id` different,
    /// verified by querying the rows directly rather than trusting a second `201` alone, exactly as
    /// this item's own Done-when demands. Fails before this item's change (the old code 409'd on the
    /// second call) and passes after.
    /// </summary>
    [Fact]
    public async Task RegisterSite_ASecondCallFromTheSameIdentity_CreatesASecondSiteAndOperatorRow()
    {
        var (token, _) = await fixture.CreateFreshUserAccessTokenAsync();

        await using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var first = await client.PostAsJsonAsync(
            "/api/v1/sites", new SitesEndpoints.RegisterSiteRequest("Acme Support", "https://shop.example.com"));
        var second = await client.PostAsJsonAsync(
            "/api/v1/sites", new SitesEndpoints.RegisterSiteRequest("Acme Support - Second Shop", "https://other.example.com"));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var firstBody = await first.Content.ReadFromJsonAsync<SitesEndpoints.RegisterSiteResponse>();
        var secondBody = await second.Content.ReadFromJsonAsync<SitesEndpoints.RegisterSiteResponse>();
        Assert.NotNull(firstBody);
        Assert.NotNull(secondBody);
        Assert.NotEqual(firstBody.SiteId, secondBody.SiteId);
        Assert.NotEqual(firstBody.OperatorId, secondBody.OperatorId);

        var externalSubjectId = ReadSubjectClaim(token);
        await using var db = fixture.CreateDbContext();
        var operatorRows = await db.Operators.Where(o => o.ExternalSubjectId == externalSubjectId).ToListAsync();
        Assert.Equal(2, operatorRows.Count);
        Assert.Contains(operatorRows, o => o.SiteId == new SiteId(firstBody.SiteId));
        Assert.Contains(operatorRows, o => o.SiteId == new SiteId(secondBody.SiteId));

        var siteRows = await db.Sites
            .Where(s => s.Id == new SiteId(firstBody.SiteId) || s.Id == new SiteId(secondBody.SiteId))
            .ToListAsync();
        Assert.Equal(2, siteRows.Count);
    }

    // `12-04` added a test here asserting this endpoint answered `403` to a platform-owner token, and
    // `12-05` removed both the refusal and the test (`adr/0063`, "Reversed in 12-05"). The identity
    // that holds both the `platform-owner` realm role and an `operators` row is not a variation on
    // this file's subject - it is its own claim, about two endpoints at once - so it lives in
    // `PlatformOwnerAsTenantTests` rather than as a fifth case here. This file keeps what it always
    // was about: what `POST /api/v1/sites` does for an ordinary self-registering identity.

    private static string ReadSubjectClaim(string jwt) =>
        new JwtSecurityTokenHandler().ReadJwtToken(jwt).Subject;

    /// <summary>A real <see cref="WebApplication"/>, not the generic <c>HostBuilder</c> seam
    /// <see cref="OperatorOidcAuthenticationTests"/>/<see cref="KeycloakIdentityPolicyTests"/> use -
    /// <c>SitesEndpoints.MapSitesEndpoints</c> (like every other endpoints file in this codebase) is
    /// typed against <see cref="WebApplication"/>, so building one here is what lets this test call
    /// the exact production mapping instead of hand-rolling a second copy of its route/handler.</summary>
    private async Task<WebApplication> BuildTestHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddRouting();
        builder.Services.AddSingleton(fixture.DataSource);
        builder.Services.AddDbContext<AgoChatDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<Npgsql.NpgsqlDataSource>()));
        builder.Services.AddScoped<IOperatorRepository, OperatorRepository>();
        builder.Services.AddScoped<ISiteRegistrationRepository, SiteRegistrationRepository>();
        builder.Services.AddScoped<ResolveOperatorIdentityHandler>();
        builder.Services.AddScoped<RegisterSiteHandler>();
        // `13-07`: OperatorIdentityClaimsTransformation now reads the active-site signal off the
        // current request - see Program.cs's own remarks on this exact registration.
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IClaimsTransformation, OperatorIdentityClaimsTransformation>();
        builder.Services.AddSingleton<IRateLimiter, FakeRateLimiter>();
        builder.Services.AddSingleton(new RegisterSiteRateLimitOptions());
        builder.Services.AddSingleton<IIdGenerator, UuidV7Generator>();
        builder.Services.AddSingleton<IClock, Ago.Platform.Hosting.SystemClock>();

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
            options.AddPolicy(
                "RequireOperatorIdentity",
                policy => policy.AddAuthenticationSchemes(JwtSchemes.Operator).RequireClaim(AgoClaimTypes.OperatorId));
            options.AddPolicy(
                "RequireKeycloakIdentity",
                policy => policy.AddAuthenticationSchemes(JwtSchemes.Operator).RequireAuthenticatedUser());
        });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        // The real production mapping - no duplicated route/handler logic.
        app.MapSitesEndpoints();
        app.MapGet("/operator-only", (HttpContext _) => Results.Ok())
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = JwtSchemes.Operator,
                Policy = "RequireOperatorIdentity",
            });

        await app.StartAsync();
        return app;
    }
}
