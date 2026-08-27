using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ago.Chat.Api.Auth;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `5-05`/`adr/0022`'s Done-when: a real Keycloak-issued token resolves to the correct
/// `OperatorId`/`SiteId` through the real authentication pipeline - not JWT-validation-in-isolation.
/// Uses a minimal `TestServer` host wired with exactly `Program.cs`'s Operator-scheme configuration
/// (`Authority`, the audience check, `OperatorIdentityClaimsTransformation`, the
/// `RequireOperatorIdentity` policy) against a single test endpoint, rather than the full
/// `Ago.Chat.Api` host (which would need Postgres, RabbitMQ, Redis, and MinIO all running just to
/// prove authentication) - the hub-connection mechanics this endpoint does not exercise
/// (query-string token forwarding, `HubOriginValidator`) are unchanged by this item and already
/// proven elsewhere (`VisitorHub`'s own tests), so nothing about *this* item's real change - claims
/// resolution - is left untested by using an HTTP endpoint instead of a full hub connection.
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class OperatorOidcAuthenticationTests(OperatorOidcFixture fixture)
{
    [Fact]
    public async Task RealKeycloakToken_ResolvesToTheCorrectOperatorAndSite()
    {
        var token = await fixture.GetDemoOperatorAccessTokenAsync();

        using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/whoami");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WhoAmIResponse>();
        Assert.Equal(fixture.SeededOperatorId.Value, body!.OperatorId);
        Assert.Equal(fixture.SeededSiteId.Value, body.SiteId);
    }

    [Fact]
    public async Task KeycloakUserWithNoMatchingOperatorRow_IsRejected()
    {
        // The orphan Keycloak user is real, but no `operators` row ever links to it.
        var token = await fixture.GetOrphanOperatorAccessTokenAsync();

        using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/whoami");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NoToken_IsRejected()
    {
        using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();

        var response = await client.GetAsync("/whoami");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TokenFromTheWrongIssuer_IsRejected()
    {
        var token = await fixture.GetWrongIssuerAccessTokenAsync();

        using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/whoami");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExpiredToken_IsRejected()
    {
        var token = await fixture.GetDemoOperatorAccessTokenAsync();
        // The test realm's access tokens live 5 seconds (keycloak-realm-import.json) - long enough
        // for every other test's own round trip, short enough to just wait past here.
        await Task.Delay(TimeSpan.FromSeconds(6));

        using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/whoami");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
                                // Default is 5 minutes - fine for production (tolerates real clock
                                // drift between hosts), but it would silently swallow
                                // ExpiredToken_IsRejected's whole premise (a token 6 seconds past
                                // its 5-second lifespan is still "valid" under a 5-minute skew).
                                ClockSkew = TimeSpan.Zero,
                            };
                        });
                    services.AddAuthorization(options => options.AddPolicy(
                        "RequireOperatorIdentity",
                        policy => policy.AddAuthenticationSchemes(JwtSchemes.Operator).RequireClaim(AgoClaimTypes.OperatorId)));
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapGet("/whoami", (HttpContext ctx) =>
                        Results.Ok(new WhoAmIResponse(ctx.User.GetOperatorId().Value, ctx.User.GetSiteId().Value)))
                        .RequireAuthorization(new AuthorizeAttribute
                        {
                            AuthenticationSchemes = JwtSchemes.Operator,
                            Policy = "RequireOperatorIdentity",
                        }));
                });
            });

        var host = await hostBuilder.StartAsync();
        return host;
    }

    private sealed record WhoAmIResponse(Guid OperatorId, Guid SiteId);
}
