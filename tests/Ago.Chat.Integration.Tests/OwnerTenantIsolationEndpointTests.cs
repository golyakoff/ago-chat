using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Owner;
using Ago.Chat.Contracts;
using Ago.Chat.Infrastructure.TenantScopeDiagnostics;
using Ago.Platform.Kernel;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `24-17`'s own Done-when: `GET /api/v1/owner/tenant-isolation` answers the platform owner and
/// refuses everyone else, proven with real Keycloak-signed tokens
/// (<see cref="OperatorOidcFixture"/>) exactly the way `OwnerSitesEndpointTests` already proves it for
/// `12-02`'s route.
///
/// <para><b>No Postgres in this file's own host, unlike that one.</b> `RequirePlatformOwner` reads
/// only the validated token's `realm_access.roles` claim (`PlatformOwnerAuthorizationHandler`'s own
/// remarks: "never queries the `operators`/`roles`/`operator_roles` tables") - this route has nothing
/// behind it that touches a database at all, so this test host registers no `DbContext`, no
/// repository and no claims transformation, and still exercises the real production
/// `OwnerTenantIsolationEndpoints.MapOwnerTenantIsolationEndpoint`/`TenantScopeInspector` pair.</para>
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class OwnerTenantIsolationEndpointTests(OperatorOidcFixture fixture)
{
    private const string Route = "/api/v1/owner/tenant-isolation";

    [Fact]
    public async Task OwnerToken_GetsTheLiveSnapshot()
    {
        var token = await fixture.GetPlatformOwnerAccessTokenAsync();

        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TenantIsolationSummaryResponse>();
        Assert.NotNull(body);
        // Real figures from the real Ago.Chat.Application.dll sitting next to this test host, not a
        // stub - the same numbers TenantScopeInspectorTests.Snapshot_AgreesWithTheDirectScan proves
        // agree with the architecture test's own IL walk.
        Assert.True(body.EntryPoints > 0);
        Assert.True(body.HandlerClasses > 0);
        Assert.True(body.RbacGated > 0);
        Assert.True(body.ExemptListed > 0);
        Assert.Empty(body.UnaccountedKeys);
        Assert.Empty(body.ExemptButAlsoLooksGated);
        // `Ago.Chat.Api`'s own real hub/route registrations aren't mapped in this stripped host (only
        // MapOwnerTenantIsolationEndpoint is), so the routes figure here is this test host's own one
        // route plus the two real hub types' reflected method counts - present and non-negative is
        // what this host can honestly assert; the full-host figure is exercised manually (see this
        // item's own report).
        Assert.True(body.RoutesAndHubMethods > 0);
        Assert.True(body.ClientSuppliedSiteIdRoutes >= 0);
    }

    // `24-17`'s "Unaccounted correctly flags a handler that is temporarily made ungated" fails-before
    // is proven at the unit level instead of here -
    // `Ago.Chat.Architecture.Tests.TenantScopeInspectorTests.Snapshot_FlagsAnUngatedHandler_WhenScanningAnAssemblyThatHasOne`
    // points `TenantScopeInspector.Scan` directly at `Ago.Chat.Architecture.Tests.dll`, which genuinely
    // carries one (`Fixtures.ForgetfulTenantScopedHandler`). Reaching the identical fixture from this
    // HTTP-level test would need this project to reference the architecture-test project purely to
    // borrow its compiled output - a coupling this file's own job (proving the real
    // `Program.cs` wiring end to end) does not need, when the fact itself is already proven directly.

    [Fact]
    public async Task OrdinaryOperatorToken_IsRejected()
    {
        var token = await fixture.GetDemoOperatorAccessTokenAsync();

        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Route)).StatusCode);
    }

    /// <summary>The same case `OwnerSitesEndpointTests.SiteConfigureHoldingAdminToken_IsRejected`
    /// proves for `12-02`'s route: a tenant's own site-wide `"Admin"`, holding every permission that
    /// exists for their one site, is still not the platform owner.</summary>
    [Fact]
    public async Task SiteConfigureHoldingAdminToken_IsRejected()
    {
        var token = await fixture.GetDemoAdminAccessTokenAsync();

        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Route)).StatusCode);
    }

    [Fact]
    public async Task NoToken_IsRejected()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Route)).StatusCode);
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

        // The production registration, Program.cs's own AddTenantScopeDiagnostics call - the real
        // TenantScopeInspector, pointed (by its own zero-argument constructor) at this test host's own
        // Ago.Chat.Application.dll.
        builder.Services.AddTenantScopeDiagnostics();

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
            // `Program.cs`'s own declaration, reproduced verbatim - the same fidelity
            // OwnerSitesEndpointTests' own host already holds itself to.
            options.AddPolicy("RequirePlatformOwner", policy => policy
                .AddAuthenticationSchemes(JwtSchemes.Operator)
                .RequireAuthenticatedUser()
                .AddRequirements(new PlatformOwnerRequirement()));
        });
        builder.Services.AddSingleton<IAuthorizationHandler, PlatformOwnerAuthorizationHandler>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        // The real production mapping - no duplicated route or policy decision.
        app.MapOwnerTenantIsolationEndpoint();

        await app.StartAsync();
        return app;
    }
}
