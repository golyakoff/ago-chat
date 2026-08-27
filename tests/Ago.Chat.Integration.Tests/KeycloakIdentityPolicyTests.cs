using System.Net;
using System.Net.Http.Headers;
using Ago.Chat.Api.Auth;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Infrastructure.Postgres;
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
/// `10-01`/`adr/0027`'s own Done-when: a token minted for a Keycloak identity with no matching
/// `operators` row is accepted by the new `RequireKeycloakIdentity` policy and rejected by the
/// existing `RequireOperatorIdentity` - proving these are two genuinely distinct enforcement points,
/// not the same check renamed. Reuses <see cref="OperatorOidcFixture"/>'s
/// <c>orphan-operator</c> Keycloak user as the stand-in for "a freshly self-registered visitor" - it
/// is already exactly that shape (a real, signature-valid Keycloak token whose `sub` resolves to no
/// `operators` row, `5-05`'s own remarks on why that user exists), so minting a second, separate
/// "self-registered" user would only duplicate it under a different name.
///
/// Same minimal-<see cref="TestServer"/> seam as <see cref="OperatorOidcAuthenticationTests"/>, wired
/// with both policies at once (`Program.cs`'s real registration for each) against two test endpoints,
/// rather than the full <c>Ago.Chat.Api</c> host.
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class KeycloakIdentityPolicyTests(OperatorOidcFixture fixture)
{
    [Fact]
    public async Task OrphanKeycloakToken_IsAcceptedByRequireKeycloakIdentity()
    {
        var token = await fixture.GetOrphanOperatorAccessTokenAsync();

        using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/keycloak-identity-only");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OrphanKeycloakToken_IsRejectedByRequireOperatorIdentity()
    {
        var token = await fixture.GetOrphanOperatorAccessTokenAsync();

        using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/operator-only");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>The mirror case, so this file proves the two policies actually differ rather than
    /// both just always allowing (or always denying) a valid Keycloak token: the *demo* operator's
    /// token - a `sub` that does resolve to an `operators` row - is accepted by both.</summary>
    [Fact]
    public async Task DemoOperatorToken_IsAcceptedByBothPolicies()
    {
        var token = await fixture.GetDemoOperatorAccessTokenAsync();

        using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var keycloakIdentityResponse = await client.GetAsync("/keycloak-identity-only");
        var operatorResponse = await client.GetAsync("/operator-only");

        Assert.Equal(HttpStatusCode.OK, keycloakIdentityResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, operatorResponse.StatusCode);
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
                    services.AddDbContext<Ago.Chat.Infrastructure.Postgres.Persistence.AgoChatDbContext>((provider, options) =>
                        options.UseNpgsql(provider.GetRequiredService<Npgsql.NpgsqlDataSource>()));
                    services.AddScoped<IOperatorRepository, OperatorRepository>();
                    services.AddScoped<ResolveOperatorIdentityHandler>();
                    services.AddHttpContextAccessor();
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
                    // `Program.cs`'s real policy pair, reproduced exactly (both differ only in
                    // whether RequireClaim(OperatorId) is present) - the whole point of this test is
                    // to prove that one line of difference actually changes enforcement.
                    services.AddAuthorization(options =>
                    {
                        options.AddPolicy(
                            "RequireOperatorIdentity",
                            policy => policy.AddAuthenticationSchemes(JwtSchemes.Operator).RequireClaim(AgoClaimTypes.OperatorId));
                        options.AddPolicy(
                            "RequireKeycloakIdentity",
                            policy => policy.AddAuthenticationSchemes(JwtSchemes.Operator).RequireAuthenticatedUser());
                    });
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/operator-only", (HttpContext _) => Results.Ok())
                            .RequireAuthorization(new AuthorizeAttribute
                            {
                                AuthenticationSchemes = JwtSchemes.Operator,
                                Policy = "RequireOperatorIdentity",
                            });
                        endpoints.MapGet("/keycloak-identity-only", (HttpContext _) => Results.Ok())
                            .RequireAuthorization(new AuthorizeAttribute
                            {
                                AuthenticationSchemes = JwtSchemes.Operator,
                                Policy = "RequireKeycloakIdentity",
                            });
                    });
                });
            });

        var host = await hostBuilder.StartAsync();
        return host;
    }
}
