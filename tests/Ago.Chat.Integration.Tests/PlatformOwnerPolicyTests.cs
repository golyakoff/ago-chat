using System.Net;
using System.Net.Http.Headers;
using Ago.Chat.Api.Auth;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
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

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `12-01`/`adr/0032`'s own Done-when, against real Keycloak-signed tokens: the `platform-owner`
/// realm role is what `RequirePlatformOwner` accepts, and *nothing else* is - not an ordinary
/// operator, not `5-08`'s site-wide `"Admin"` operator holding `site:configure`, not a valid
/// Keycloak identity with no `operators` row at all, not an anonymous request.
///
/// Same minimal-<see cref="TestServer"/> seam as <see cref="KeycloakIdentityPolicyTests"/>, wired
/// with all three of `Program.cs`'s policies at once against three test endpoints - the point is the
/// *difference* between them, which a file testing one policy in isolation could not show.
/// <see cref="OperatorIdentityClaimsTransformation"/> is registered here exactly as the real host
/// registers it, deliberately: the owner token must pass while that transformation is running and
/// resolving nothing, which is the concrete form of "this policy does not depend on it."
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class PlatformOwnerPolicyTests(OperatorOidcFixture fixture)
{
    private const string PlatformOwnerRoute = "/platform-owner-only";
    private const string OperatorRoute = "/operator-only";
    private const string KeycloakIdentityRoute = "/keycloak-identity-only";

    [Fact]
    public async Task PlatformOwnerToken_IsAcceptedByRequirePlatformOwner()
    {
        var token = await fixture.GetPlatformOwnerAccessTokenAsync();

        var response = await GetAsync(PlatformOwnerRoute, token);

        Assert.Equal(HttpStatusCode.OK, response);
    }

    /// <summary>The other half of "structurally separate": the owner identity has no `operators` row,
    /// so `RequireOperatorIdentity` rejects the very token `RequirePlatformOwner` accepts. Being the
    /// platform owner is not a superset of being an operator - it is a different axis entirely.</summary>
    [Fact]
    public async Task PlatformOwnerToken_IsRejectedByRequireOperatorIdentity()
    {
        var token = await fixture.GetPlatformOwnerAccessTokenAsync();

        var response = await GetAsync(OperatorRoute, token);

        Assert.Equal(HttpStatusCode.Forbidden, response);
    }

    [Fact]
    public async Task OrdinaryOperatorToken_IsRejectedByRequirePlatformOwner()
    {
        var token = await fixture.GetDemoOperatorAccessTokenAsync();

        var response = await GetAsync(PlatformOwnerRoute, token);

        Assert.Equal(HttpStatusCode.Forbidden, response);
    }

    /// <summary>The case this item exists to make impossible: `5-08`'s `"Admin"` role really does
    /// grant `site:configure` - proven here against the same <see cref="PermissionChecker"/> every
    /// handler uses, not asserted - and the platform-owner boundary still rejects it. A permission
    /// granted broadly inside one site is not, and cannot become, cross-tenant access.</summary>
    [Fact]
    public async Task SiteConfigureHoldingAdminToken_IsRejectedByRequirePlatformOwner_ThoughItIsARealOperator()
    {
        await using (var db = fixture.CreateDbContext())
        {
            var checker = new PermissionChecker(db);
            Assert.True(await checker.HasPermissionAsync(
                fixture.SeededAdminOperatorId, fixture.SeededSiteId, Permission.SiteConfigure, CancellationToken.None));
        }

        var token = await fixture.GetDemoAdminAccessTokenAsync();

        Assert.Equal(HttpStatusCode.OK, await GetAsync(OperatorRoute, token));
        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync(PlatformOwnerRoute, token));
    }

    /// <summary>`10-01`'s `RequireKeycloakIdentity`-eligible state - a genuine, signature-valid
    /// Keycloak token whose `sub` resolves to no `operators` row. Accepted by the weakest policy,
    /// rejected by the owner one: "any valid Keycloak token" is not the check being made.</summary>
    [Fact]
    public async Task OrphanKeycloakToken_IsRejectedByRequirePlatformOwner()
    {
        var token = await fixture.GetOrphanOperatorAccessTokenAsync();

        Assert.Equal(HttpStatusCode.OK, await GetAsync(KeycloakIdentityRoute, token));
        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync(PlatformOwnerRoute, token));
    }

    // A token from the wrong issuer is deliberately not re-tested here: it is rejected by the
    // Operator *scheme* before any policy runs, which `OperatorOidcAuthenticationTests.
    // TokenFromTheWrongIssuer_IsRejected` already proves against this same scheme. Calling the
    // fixture's `GetWrongIssuerAccessTokenAsync` a second time would also try to create the same
    // throwaway realm twice on the shared container and 409.

    [Fact]
    public async Task NoToken_IsRejectedByRequirePlatformOwner()
    {
        var response = await GetAsync(PlatformOwnerRoute, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response);
    }

    private async Task<HttpStatusCode> GetAsync(string route, string? token)
    {
        using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await client.GetAsync(route);
        return response.StatusCode;
    }

    private async Task<IHost> BuildTestHostAsync()
    {
        var hostBuilder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton(fixture.DataSource);
                    services.AddDbContext<AgoChatDbContext>((provider, options) =>
                        options.UseNpgsql(provider.GetRequiredService<Npgsql.NpgsqlDataSource>()));
                    services.AddScoped<IOperatorRepository, OperatorRepository>();
                    services.AddScoped<ResolveOperatorIdentityHandler>();
                    services.AddSingleton<IClaimsTransformation, OperatorIdentityClaimsTransformation>();

                    services.AddAuthentication()
                        .AddJwtBearer(JwtSchemes.Operator, options =>
                        {
                            options.MapInboundClaims = false;
                            options.Authority = fixture.KeycloakAuthority;
                            options.RequireHttpsMetadata = false;
                            options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
                            {
                                ValidateAudience = true,
                                ValidAudience = OperatorOidcFixture.ClientId,
                                ValidateLifetime = true,
                                ClockSkew = TimeSpan.Zero,
                            };
                        });
                    // `Program.cs`'s real policy trio, reproduced exactly.
                    services.AddAuthorization(options =>
                    {
                        options.AddPolicy(
                            "RequireOperatorIdentity",
                            policy => policy.AddAuthenticationSchemes(JwtSchemes.Operator).RequireClaim(AgoClaimTypes.OperatorId));
                        options.AddPolicy(
                            "RequireKeycloakIdentity",
                            policy => policy.AddAuthenticationSchemes(JwtSchemes.Operator).RequireAuthenticatedUser());
                        options.AddPolicy(
                            "RequirePlatformOwner",
                            policy => policy.AddAuthenticationSchemes(JwtSchemes.Operator)
                                .RequireAuthenticatedUser()
                                .AddRequirements(new PlatformOwnerRequirement()));
                    });
                    services.AddSingleton<IAuthorizationHandler, PlatformOwnerAuthorizationHandler>();
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet(OperatorRoute, (HttpContext _) => Results.Ok())
                            .RequireAuthorization(new AuthorizeAttribute
                            {
                                AuthenticationSchemes = JwtSchemes.Operator,
                                Policy = "RequireOperatorIdentity",
                            });
                        endpoints.MapGet(KeycloakIdentityRoute, (HttpContext _) => Results.Ok())
                            .RequireAuthorization(new AuthorizeAttribute
                            {
                                AuthenticationSchemes = JwtSchemes.Operator,
                                Policy = "RequireKeycloakIdentity",
                            });
                        endpoints.MapGet(PlatformOwnerRoute, (HttpContext _) => Results.Ok())
                            .RequireAuthorization(new AuthorizeAttribute
                            {
                                AuthenticationSchemes = JwtSchemes.Operator,
                                Policy = "RequirePlatformOwner",
                            });
                    });
                });
            });

        var host = await hostBuilder.StartAsync();
        return host;
    }
}
