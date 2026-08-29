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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `13-03`: `OperatorIdentityClaimsTransformation`'s own new sign-in-blocking behaviour, proven with a
/// real token against the real resolution path - `OperatorOidcAuthenticationTests`' own technique
/// (`OperatorOidcFixture`, a minimal `TestServer` host wired with exactly `Program.cs`'s Operator-scheme
/// configuration), reused here for a second real Keycloak identity created fresh for this test class
/// specifically so toggling its own `operators` row never touches the shared demo operator every other
/// test in this collection depends on.
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class OperatorSeatAssignmentAuthenticationTests(OperatorOidcFixture fixture)
{
    [Fact]
    public async Task RealToken_WhoseOperatorRowHasHoldsSeatToggledOff_ResolvesToNoOperatorIdClaim()
    {
        var (token, externalSubjectId) = await CreateFreshOperatorAsync();

        using (var host = await BuildTestHostAsync())
        using (var client = host.GetTestClient())
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var before = await client.GetAsync("/whoami");
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var op = await db.Operators.SingleAsync(o => o.ExternalSubjectId == externalSubjectId);
            op.ToggleSeat(false);
            await db.SaveChangesAsync();
        }

        using (var host = await BuildTestHostAsync())
        using (var client = host.GetTestClient())
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var after = await client.GetAsync("/whoami");

            // The same real token that resolved OK a moment ago now carries no OperatorId claim at
            // all - RequireOperatorIdentity's own RequireClaim check refuses it, the exact same shape
            // as no operators row ever having existed (KeycloakUserWithNoMatchingOperatorRow_IsRejected's
            // own precedent).
            Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
        }
    }

    [Fact]
    public async Task RealToken_WhoseOperatorRowWasRemoved_ResolvesToNoOperatorIdClaim()
    {
        var (token, externalSubjectId) = await CreateFreshOperatorAsync();

        await using (var db = fixture.CreateDbContext())
        {
            var op = await db.Operators.SingleAsync(o => o.ExternalSubjectId == externalSubjectId);
            op.Remove(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/whoami");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>A brand-new Keycloak user (`OperatorOidcFixture.CreateFreshUserAccessTokenAsync`) plus
    /// a real <c>operators</c> row this test writes itself, on a site of its own - never the shared
    /// <c>SeededOperatorId</c>, so this test can freely mutate <c>HoldsSeat</c>/<c>RemovedAt</c> without
    /// making the rest of the collection order-dependent.</summary>
    private async Task<(string Token, string ExternalSubjectId)> CreateFreshOperatorAsync()
    {
        var (token, username) = await fixture.CreateFreshUserAccessTokenAsync();
        var externalSubjectId = await fixture.GetUserIdAsync(username);

        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId: externalSubjectId));
        await db.SaveChangesAsync();

        return (token, externalSubjectId);
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
                            options.TokenValidationParameters = new TokenValidationParameters
                            {
                                ValidateAudience = true,
                                ValidAudience = OperatorOidcFixture.ClientId,
                                ValidateLifetime = true,
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

        return await hostBuilder.StartAsync();
    }

    private sealed record WhoAmIResponse(Guid OperatorId, Guid SiteId);
}
