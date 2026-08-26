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

    [Fact]
    public async Task RegisterSite_ASecondCallFromTheSameIdentity_Returns409_NotASecondSite()
    {
        var (token, _) = await fixture.CreateFreshUserAccessTokenAsync();

        await using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var first = await client.PostAsJsonAsync(
            "/api/v1/sites", new SitesEndpoints.RegisterSiteRequest("Acme Support", "https://shop.example.com"));
        var second = await client.PostAsJsonAsync(
            "/api/v1/sites", new SitesEndpoints.RegisterSiteRequest("Acme Support Again", "https://other.example.com"));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var externalSubjectId = ReadSubjectClaim(token);
        await using var db = fixture.CreateDbContext();
        var operatorCount = await db.Operators.CountAsync(o => o.ExternalSubjectId == externalSubjectId);
        Assert.Equal(1, operatorCount);
    }

    /// <summary>
    /// `12-04`: the half of that item that a client-side gate cannot cover. The console hides the
    /// onboarding form from a platform-owner identity, but a bookmark, a back button or a second tab
    /// all reach this endpoint directly - and what it commits (a `Site`, both roles, an `Operator`
    /// row for the owner's `sub`, its role assignments) is a state change nothing in this product can
    /// undo. `adr/0032` gives the platform owner no `operators` row deliberately; a successful call
    /// here would give it one permanently.
    ///
    /// <para>This is the test the item asks to fail if the check is removed: it goes through the real
    /// <c>SitesEndpoints.MapSitesEndpoints</c> mapping, so deleting
    /// <c>.RequireAuthorization(AuthorizationPolicies.NotThePlatformOwner)</c> from that route (or
    /// inverting <c>PlatformOwnerRealmRole.IsHeldBy</c>) turns the `403` into a `201` and the empty
    /// row count into one. The `403` alone would not be enough evidence - a policy that refused for
    /// some *other* reason would produce the same status - so the row count is asserted too, and
    /// <see cref="RegisterSite_WithARealKeycloakToken_CreatesOneSiteBothRolesOneOperatorAndBothOperatorRoles"/>
    /// above is what proves the same route still says `201` to the identity it is actually for.</para>
    /// </summary>
    [Fact]
    public async Task RegisterSite_WithThePlatformOwnersToken_Is403AndWritesNothing()
    {
        var token = await fixture.GetPlatformOwnerAccessTokenAsync();
        var externalSubjectId = ReadSubjectClaim(token);

        await using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync(
            "/api/v1/sites", new SitesEndpoints.RegisterSiteRequest("The Platform Itself", "https://shop.example.com"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // Refused *before* the transaction, not rolled back after it - the platform owner's `sub`
        // resolves to no operator, which is exactly the state `adr/0032` requires it to stay in.
        await using var db = fixture.CreateDbContext();
        Assert.Equal(0, await db.Operators.CountAsync(o => o.ExternalSubjectId == externalSubjectId));
        Assert.Equal(0, await db.Sites.CountAsync(s => s.Name == "The Platform Itself"));
    }

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
