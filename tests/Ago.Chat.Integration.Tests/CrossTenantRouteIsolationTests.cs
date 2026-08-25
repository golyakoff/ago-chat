using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Webhooks;
using Ago.Chat.Api.WidgetConfig;
using Ago.Chat.Application.UseCases.GetWebhookDeliveries;
using Ago.Chat.Application.UseCases.GetWidgetConfig;
using Ago.Chat.Application.UseCases.ListWebhookEndpoints;
using Ago.Chat.Application.UseCases.RegisterWebhookEndpoint;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Application.UseCases.RevokeWebhookEndpoint;
using Ago.Chat.Application.UseCases.UpdateWidgetConfig;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `17-01`: the two route groups in this codebase that take a <b>client-supplied</b> <c>site_id</c> -
/// <c>/api/v1/sites/{siteId}/widget-config</c> and <c>/api/v1/sites/{siteId}/webhooks/...</c> - proven
/// to refuse an operator of one tenant naming another, at the level where the composition is real.
///
/// <para><b>Why full HTTP with a real Keycloak and a real Postgres, rather than a handler test with a
/// fake checker.</b> A handler test proves "when the checker says no, the handler refuses" - a
/// different property from "an operator of another site is refused", and one that stays green even if
/// the endpoint stopped passing the route's <c>siteId</c> to the handler at all. Everything here is
/// the production article: the real <c>MapWidgetConfigEndpoints</c>/<c>MapWebhookEndpoints</c>
/// mappings, the real <c>RequireOperatorIdentity</c> policy, the real
/// <c>OperatorIdentityClaimsTransformation</c> resolving a real Keycloak <c>sub</c> to a real
/// <c>operators</c> row, and the real <c>PermissionChecker</c> reading real <c>roles</c>/
/// <c>operator_roles</c>. The only thing the test supplies is the data.</para>
///
/// <para><b>The attacker is deliberately privileged.</b> Every refusal below would also happen for an
/// operator who simply holds no permissions at all, which would prove nothing - so
/// <see cref="SetUpAsync"/> grants the caller <c>site:configure</c> <em>and</em> <c>webhook:manage</c>
/// on their own site, and <see cref="TheCallerReallyHoldsBothPermissions_OnTheirOwnSite"/> plus the
/// same-site positive control on each route establish that those grants are real and that this host is
/// wired correctly. A test whose 403s came from a misconfigured server would be worthless.</para>
///
/// <para>A fresh Keycloak identity and a fresh pair of sites per set-up, never the fixture's shared
/// <c>demo-operator</c>/<c>SeededSiteId</c>: granting the shared operator two more permissions would
/// change what every other class in this collection is testing.</para>
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class CrossTenantRouteIsolationTests(OperatorOidcFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly WebhookSecretCipherOptions CipherOptions = new()
    {
        SecretEncryptionKey = "Vg1G2KjonUB1uH8trETJzr30EPoeqt0YRGzYibDKy1o=",
    };

    /// <param name="CallerSiteId">The tenant the caller is an operator of, and holds every relevant
    /// permission for.</param>
    /// <param name="VictimSiteId">A different tenant, which the caller has no relationship to at
    /// all.</param>
    /// <param name="VictimEndpointId">A webhook endpoint belonging to the victim, so the
    /// belongs-to-site branch can be reached with a real id rather than a made-up one.</param>
    private sealed record Scenario(
        string AccessToken, OperatorId CallerOperatorId, SiteId CallerSiteId, SiteId VictimSiteId,
        WebhookEndpointId VictimEndpointId);

    [Fact]
    public async Task TheCallerReallyHoldsBothPermissions_OnTheirOwnSite()
    {
        var scenario = await SetUpAsync();

        await using var db = fixture.CreateDbContext();
        var checker = new PermissionChecker(db);

        // Proven through the same PermissionChecker the endpoints use, not assumed from the seed.
        Assert.True(await checker.HasPermissionAsync(
            scenario.CallerOperatorId, scenario.CallerSiteId, Permission.SiteConfigure, CancellationToken.None));
        Assert.True(await checker.HasPermissionAsync(
            scenario.CallerOperatorId, scenario.CallerSiteId, Permission.WebhookManage, CancellationToken.None));
        // And holds neither on the tenant every test below names.
        Assert.False(await checker.HasPermissionAsync(
            scenario.CallerOperatorId, scenario.VictimSiteId, Permission.SiteConfigure, CancellationToken.None));
        Assert.False(await checker.HasPermissionAsync(
            scenario.CallerOperatorId, scenario.VictimSiteId, Permission.WebhookManage, CancellationToken.None));
    }

    [Fact]
    public async Task WidgetConfigRoutes_RefuseAnotherTenantsSite_AndChangeNothing()
    {
        var scenario = await SetUpAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, scenario.AccessToken);

        var own = $"/api/v1/sites/{scenario.CallerSiteId.Value}/widget-config";
        var victim = $"/api/v1/sites/{scenario.VictimSiteId.Value}/widget-config";

        // Positive control first: the same caller, the same client, their own site - so a 403 below
        // means "this site, not you", not "this server refuses everyone".
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(own)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(victim)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(client, victim, "#ff0000", "BottomLeft")).StatusCode);

        // The refused write really did not land - a 403 that still mutated the row would be the worse
        // bug, and status codes alone cannot tell the two apart.
        await using var db = fixture.CreateDbContext();
        var victimSite = await new SiteRepository(db).GetByIdAsync(scenario.VictimSiteId, CancellationToken.None);
        Assert.Equal(VictimColorHex, victimSite!.WidgetConfig.PrimaryColorHex);
        Assert.Equal(Position.BottomRight, victimSite.WidgetConfig.Position);
    }

    [Fact]
    public async Task WebhookRoutes_RefuseAnotherTenantsSite()
    {
        var scenario = await SetUpAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, scenario.AccessToken);

        var own = $"/api/v1/sites/{scenario.CallerSiteId.Value}/webhooks";
        var victim = $"/api/v1/sites/{scenario.VictimSiteId.Value}/webhooks";

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(own)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(victim)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync(victim, new { url = "https://attacker.example.com/hooks" })).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.DeleteAsync($"{victim}/{scenario.VictimEndpointId.Value}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.GetAsync($"{victim}/{scenario.VictimEndpointId.Value}/deliveries")).StatusCode);

        // Nothing was registered against the victim under cover of the refusal.
        await using var db = fixture.CreateDbContext();
        var victimEndpoints = await new WebhookEndpointRepository(db)
            .GetAllForSiteAsync(scenario.VictimSiteId, CancellationToken.None);
        Assert.Equal(scenario.VictimEndpointId, Assert.Single(victimEndpoints).Id);
        Assert.True(victimEndpoints[0].Active);
    }

    /// <summary>
    /// The other half of the same isolation, and the one a permission check cannot reach: the caller
    /// names <b>their own</b> site - so <c>webhook:manage</c> genuinely passes - and pairs it with
    /// another tenant's endpoint id. Only <c>endpoint.SiteId != query.SiteId</c> stands here, in
    /// <c>GetWebhookDeliveriesHandler</c> and <c>RevokeWebhookEndpointHandler</c>. `17-01` gave the
    /// first of those two a unit test that fails when the branch is removed; this is the same
    /// property over real HTTP.
    /// </summary>
    [Fact]
    public async Task WebhookRoutes_RefuseAnotherTenantsEndpointIdUnderTheCallersOwnSite()
    {
        var scenario = await SetUpAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, scenario.AccessToken);

        var own = $"/api/v1/sites/{scenario.CallerSiteId.Value}/webhooks";

        // NotFound, not Forbidden: another tenant's endpoint must be indistinguishable from one that
        // does not exist - the permission check has already passed by this point, so a 403 here would
        // be confirming the endpoint exists somewhere.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync($"{own}/{scenario.VictimEndpointId.Value}/deliveries")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.DeleteAsync($"{own}/{scenario.VictimEndpointId.Value}")).StatusCode);

        await using var db = fixture.CreateDbContext();
        var stored = await new WebhookEndpointRepository(db)
            .GetByIdAsync(scenario.VictimEndpointId, CancellationToken.None);
        Assert.True(stored!.Active, "the victim's endpoint must not have been revoked");
    }

    private const string VictimColorHex = "#0000ff";

    private static Task<HttpResponseMessage> Put(HttpClient client, string route, string? colorHex, string position) =>
        client.PutAsync(route, new StringContent(
            $$"""{"primaryColorHex":{{(colorHex is null ? "null" : $"\"{colorHex}\"")}},"position":"{{position}}"}""",
            Encoding.UTF8,
            "application/json"));

    /// <summary>Two unrelated tenants and one real Keycloak identity that operates the first of them,
    /// holding every permission the routes under test consult - for that site only.</summary>
    private async Task<Scenario> SetUpAsync()
    {
        var (accessToken, username) = await fixture.CreateFreshUserAccessTokenAsync();
        // Keycloak's own user id is the `sub` the token carries and the value
        // OperatorIdentityClaimsTransformation resolves against `operators.external_subject_id`.
        var externalSubjectId = await fixture.GetUserIdAsync(username);

        var callerSiteId = new SiteId(Guid.NewGuid());
        var callerOperatorId = new OperatorId(Guid.NewGuid());
        var victimSiteId = new SiteId(Guid.NewGuid());
        var victimEndpointId = new WebhookEndpointId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(callerSiteId, $"site_{callerSiteId.Value:N}", []));
            db.Operators.Add(new Operator(
                callerOperatorId, callerSiteId, OperatorStatus.Online, capacity: 5,
                externalSubjectId: externalSubjectId));
            var roleId = Guid.NewGuid();
            db.Roles.Add(new RoleRecord
            {
                Id = roleId,
                SiteId = callerSiteId,
                Name = "Admin",
                Permissions = [Permission.SiteConfigure.Value, Permission.WebhookManage.Value],
            });
            db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = callerOperatorId, RoleId = roleId });

            var victim = new Site(victimSiteId, $"site_{victimSiteId.Value:N}", []);
            // A distinctive config, so "the refused PUT changed nothing" is a real comparison rather
            // than one against whatever the default happens to be.
            victim.UpdateWidgetConfig(new WidgetConfig(VictimColorHex, Position.BottomRight), Now);
            victim.ClearDomainEvents();
            db.Sites.Add(victim);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            var endpoint = WebhookEndpoint.Register(
                victimEndpointId, victimSiteId, new Uri("https://victim.example.com/hooks"),
                new WebhookSecretCipher(CipherOptions).Encrypt("whsec_victim"), Now);
            await new WebhookEndpointRepository(db).SaveAsync(endpoint, CancellationToken.None);
        }

        return new Scenario(accessToken, callerOperatorId, callerSiteId, victimSiteId, victimEndpointId);
    }

    private static HttpClient CreateClient(WebApplication host, string token)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>The production wiring for these two route groups: the same concrete adapters
    /// <c>AddPostgresPersistence</c> binds (so <c>PermissionChecker</c> and every repository here are
    /// the real ones), the use cases <c>ChatModule</c> registers for them, and <c>Program.cs</c>'s
    /// <c>RequireOperatorIdentity</c> policy reproduced verbatim.
    ///
    /// <para>Registered one by one rather than by calling <c>AddPostgresPersistence</c> itself: that
    /// extension takes a connection <em>string</em> and builds its own data source, and
    /// <c>NpgsqlDataSource.ConnectionString</c> redacts the password, so a host built from it cannot
    /// authenticate against the fixture's container. Reusing the fixture's already-built data source
    /// is the same seam <see cref="OwnerSitesEndpointTests"/> and <see cref="SiteRegistrationTests"/>
    /// already use.</para></summary>
    private async Task<WebApplication> BuildTestHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();

        builder.Services.AddSingleton(fixture.DataSource);
        builder.Services.AddDbContext<AgoChatDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<Npgsql.NpgsqlDataSource>()));
        builder.Services.AddScoped<Ago.Chat.Application.Abstractions.IOperatorRepository, OperatorRepository>();
        builder.Services.AddScoped<Ago.Chat.Application.Abstractions.ISiteRepository, SiteRepository>();
        builder.Services.AddScoped<Ago.Chat.Application.Abstractions.IPermissionChecker, PermissionChecker>();
        builder.Services.AddScoped<Ago.Chat.Application.Abstractions.IWebhookEndpointRepository, WebhookEndpointRepository>();
        builder.Services.AddScoped<Ago.Chat.Application.Abstractions.IWebhookDeliveryReadStore, WebhookDeliveryReadStore>();
        builder.Services.AddSingleton<Ago.Chat.Application.Abstractions.IWebhookSecretGenerator, WebhookSecretGenerator>();
        builder.Services.AddScoped<Ago.Chat.Application.Abstractions.IWebhookSecretCipher, WebhookSecretCipher>();
        builder.Services.AddOutboxInbox<AgoChatDbContext>();
        builder.Services.AddSingleton(CipherOptions);
        // The real clock, as a host registers it - nothing here asserts on a timestamp, and a frozen
        // one would only invite a reader to wonder what it was for.
        builder.Services.AddSingleton<IClock, Ago.Platform.Hosting.SystemClock>();
        builder.Services.AddSingleton<IIdGenerator, UuidV7Generator>();

        builder.Services.AddScoped<ResolveOperatorIdentityHandler>();
        builder.Services.AddScoped<GetWidgetConfigHandler>();
        builder.Services.AddScoped<UpdateWidgetConfigHandler>();
        builder.Services.AddScoped<RegisterWebhookEndpointHandler>();
        builder.Services.AddScoped<ListWebhookEndpointsHandler>();
        builder.Services.AddScoped<RevokeWebhookEndpointHandler>();
        builder.Services.AddScoped<GetWebhookDeliveriesHandler>();
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
            options.AddPolicy("RequireOperatorIdentity", policy => policy
                .AddAuthenticationSchemes(JwtSchemes.Operator)
                .RequireClaim(AgoClaimTypes.OperatorId)));

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapWidgetConfigEndpoints();
        app.MapWebhookEndpoints();

        await app.StartAsync();
        return app;
    }
}
