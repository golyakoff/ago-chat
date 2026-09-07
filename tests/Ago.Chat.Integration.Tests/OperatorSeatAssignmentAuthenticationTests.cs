using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ago.Chat.Api.Auth;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetOperatorTeam;
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
    /// <summary>`23-71`: the ordinary-operator half of the rule, unchanged from `13-03` - a seat
    /// released with no `site:manage_operators` grant behind it stays refused. The complementary,
    /// newly-added case (an administrator, same release, still resolves) is
    /// <see cref="RealToken_ForAnAdministratorWithNoSeat_SignsIn_AndReachesTheOperatorsTeamScreen"/>
    /// below.</summary>
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

    /// <summary>
    /// `23-71`: the item's own central proof, demonstrated rather than asserted - both Done-when items
    /// naming an administrative route, in one real end-to-end pass. A real Keycloak token whose
    /// `operators` row holds no seat (`HoldsSeat: false`) but does hold this site's own
    /// `site:manage_operators`, exactly `decisions/0006`'s "the owner and as many operators as are
    /// paid for" restored: (1) <c>/whoami</c> still returns `200` - the resolution path adds an
    /// `OperatorId` claim despite the seat, unlike the ordinary-operator case right above; (2) the same
    /// token then reaches <c>/team</c> - the real <c>GetOperatorTeamHandler</c>, the exact handler
    /// behind the console's own operators-team screen (`OperatorsTeamPage`, `ago-console`) - and the
    /// returned roster includes this operator's own row with `HoldsSeat: false`, proving the permission
    /// check inside that handler passed for real, not merely that the outer policy let the request
    /// through.
    /// </summary>
    [Fact]
    public async Task RealToken_ForAnAdministratorWithNoSeat_SignsIn_AndReachesTheOperatorsTeamScreen()
    {
        var (token, siteId, operatorId) = await CreateFreshSeatlessAdministratorAsync();

        using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var whoami = await client.GetAsync("/whoami");
        Assert.Equal(HttpStatusCode.OK, whoami.StatusCode);
        var identity = await whoami.Content.ReadFromJsonAsync<WhoAmIResponse>();
        Assert.Equal(operatorId.Value, identity!.OperatorId);
        Assert.Equal(siteId.Value, identity.SiteId);

        var team = await client.GetAsync("/team");
        Assert.Equal(HttpStatusCode.OK, team.StatusCode);
        var roster = await team.Content.ReadFromJsonAsync<OperatorTeamResponse>();
        var self = Assert.Single(roster!.Operators, m => m.OperatorId == operatorId.Value);
        Assert.False(self.HoldsSeat);
    }

    /// <summary>The seatless-administrator twin of <see cref="CreateFreshOperatorAsync"/> - a fresh
    /// Keycloak identity, a real `operators` row with `HoldsSeat: false`, and a real `Role`/
    /// `OperatorRoleRecord` granting `site:manage_operators` for its own site (the exact shape
    /// `RegisterSiteHandler`'s own `AdminRolePermissions` seeds for a real owner, restated by hand
    /// here since this test host has no registration handler to run).</summary>
    private async Task<(string Token, SiteId SiteId, OperatorId OperatorId)> CreateFreshSeatlessAdministratorAsync()
    {
        var (token, username) = await fixture.CreateFreshUserAccessTokenAsync();
        var externalSubjectId = await fixture.GetUserIdAsync(username);

        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var roleId = Guid.NewGuid();

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Operators.Add(new Operator(
            operatorId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId: externalSubjectId, holdsSeat: false));
        db.Roles.Add(new RoleRecord
        {
            Id = roleId,
            SiteId = siteId,
            Name = "Admin",
            Permissions = [Permission.SiteManageOperators.Value],
        });
        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
        await db.SaveChangesAsync();

        return (token, siteId, operatorId);
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
                    // `23-71`: ResolveOperatorIdentityHandler now composes IPermissionChecker - a
                    // seatless operator's own site:manage_operators grant is what lets them sign in at
                    // all, so this class's own new tests need the real permission-resolution path, not
                    // a stub.
                    services.AddScoped<IPermissionChecker, PermissionChecker>();
                    services.AddScoped<ResolveOperatorIdentityHandler>();
                    // `23-71`: the real handler behind the console's own operators-team screen -
                    // RealToken_ForAnAdministratorWithNoSeat_SignsIn_AndReachesTheOperatorsTeamScreen's
                    // own "reaches an administrative route" proof.
                    services.AddScoped<IOperatorTeamReadStore, OperatorTeamReadStore>();
                    services.AddScoped<GetOperatorTeamHandler>();
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
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/whoami", (HttpContext ctx) =>
                            Results.Ok(new WhoAmIResponse(ctx.User.GetOperatorId().Value, ctx.User.GetSiteId().Value)))
                            .RequireAuthorization(new AuthorizeAttribute
                            {
                                AuthenticationSchemes = JwtSchemes.Operator,
                                Policy = "RequireOperatorIdentity",
                            });

                        // `23-71`: the real operators-team read, gated by the real
                        // GetOperatorTeamHandler - a stand-in for OperatorsEndpoints' own
                        // `GET /api/v1/team` route, close enough to prove a seatless administrator
                        // reaches a real administrative handler, not just this test's own `/whoami`.
                        endpoints.MapGet("/team", async (HttpContext ctx, GetOperatorTeamHandler handler) =>
                        {
                            var result = await handler.HandleAsync(
                                new GetOperatorTeam(ctx.User.GetOperatorId(), ctx.User.GetSiteId()), ctx.RequestAborted);
                            return result.IsSuccess
                                ? Results.Ok(result.Value)
                                : Results.Problem(statusCode: StatusCodes.Status403Forbidden, detail: result.Error!.Value.Message);
                        }).RequireAuthorization(new AuthorizeAttribute
                        {
                            AuthenticationSchemes = JwtSchemes.Operator,
                            Policy = "RequireOperatorIdentity",
                        });
                    });
                });
            });

        return await hostBuilder.StartAsync();
    }

    private sealed record WhoAmIResponse(Guid OperatorId, Guid SiteId);
}
