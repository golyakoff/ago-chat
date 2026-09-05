using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Modules;
using Ago.Chat.Api.Owner;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.EnableModuleForSite;
using Ago.Chat.Application.UseCases.EnableModuleForSiteAsOwner;
using Ago.Chat.Application.UseCases.ListEnabledModulesForSite;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Application.UseCases.RevokeModuleForSite;
using Ago.Chat.Application.UseCases.RevokeModuleForSiteAsOwner;
using Ago.Chat.Application.UseCases.RotateModuleCredential;
using Ago.Chat.Application.UseCases.VerifyModuleRegistration;
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
/// `22-17`'s own Done-when, over a real HTTP pipeline, a real Postgres and real Keycloak-signed
/// tokens (<see cref="OperatorOidcFixture"/>): the platform owner can grant a module to a tenant with
/// no payment, the grant is distinguishable from a self-service purchase wherever either is recorded,
/// revoking it works, and neither an ordinary operator nor a site-wide admin can reach this surface.
///
/// <para>Both <see cref="ModuleEndpoints.MapModuleEndpoints"/> (the tenant's own self-service route,
/// `RequireOperatorIdentity`) and <see cref="OwnerModuleEndpoints.MapOwnerModuleEndpoints"/> (the
/// owner's route, `RequirePlatformOwner`) are mapped on the same host in this file - deliberately,
/// because the audit-distinction claim can only be proven by comparing what each path writes, not by
/// reading either route's own behaviour in isolation.</para>
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class OwnerModuleEndpointsTests(OperatorOidcFixture fixture)
{
    private string SelfServiceRoute => $"/api/v1/sites/{fixture.SeededSiteId.Value}/modules";

    private string OwnerRoute => $"/api/v1/owner/sites/{fixture.SeededSiteId.Value}/modules";

    /// <summary>
    /// The item's own first Done-when, end to end: a call the tenant could not make before (their
    /// module list is empty for this key) succeeds after the owner grants it - through the real
    /// route, with no row inserted by hand on either side of the assertion.
    /// </summary>
    [Fact]
    public async Task OwnerToken_GrantsAModule_AndTheTenantsOwnListingShowsIt()
    {
        var moduleKey = UniqueModuleKey();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());
        var operatorClient = CreateClient(host, await fixture.GetDemoAdminAccessTokenAsync());

        // Before: the tenant's own module list has no such key.
        var before = await GetModulesAsync(operatorClient);
        Assert.DoesNotContain(before.Modules, m => m.ModuleKey == moduleKey);

        var grantResponse = await ownerClient.PutAsJsonAsync(
            OwnerRoute, new OwnerModuleEndpoints.GrantModuleRequest
            {
                ModuleKey = moduleKey,
                TriggerWords = ["/owner-granted"],
                EntryPoint = "https://calendar.example.com",
                Credential = "an-owner-minted-secret-of-sixteen-plus-chars",
                ProvisioningSecret = "a-provisioning-secret-of-sixteen-plus-chars",
                ExpiresAt = null,
            });
        Assert.Equal(HttpStatusCode.OK, grantResponse.StatusCode);

        // After: the identical call, through the tenant's own operator-facing GET, now lists it -
        // proving the grant is real, not merely accepted by the owner's own route.
        var after = await GetModulesAsync(operatorClient);
        var granted = Assert.Single(after.Modules, m => m.ModuleKey == moduleKey);
        Assert.Equal(["/owner-granted"], granted.TriggerWords);
    }

    /// <summary>The audit distinction, proven by doing both and comparing - not asserted from one
    /// side alone. A self-service enable and an owner grant land in the identical listing with the
    /// identical shape, distinguished only by <see cref="ModuleEndpoints.EnableModuleResponse.GrantedByOwner"/>.</summary>
    [Fact]
    public async Task GrantedByOwner_DistinguishesAnOwnerGrant_FromATenantsOwnPurchase()
    {
        var purchasedKey = UniqueModuleKey();
        var grantedKey = UniqueModuleKey();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());
        var operatorClient = CreateClient(host, await fixture.GetDemoAdminAccessTokenAsync());

        // The tenant buys module A themselves, through the ordinary self-service route.
        var purchase = await operatorClient.PutAsJsonAsync(
            SelfServiceRoute, new ModuleEndpoints.EnableModuleRequest(
                purchasedKey, ["/purchased"], "https://faq.example.com",
                "a-tenant-minted-secret-of-sixteen-plus-chars", "a-provisioning-secret-of-sixteen-plus-chars"));
        Assert.Equal(HttpStatusCode.OK, purchase.StatusCode);

        // The owner grants module B, with no payment, through the owner-only route.
        var grant = await ownerClient.PutAsJsonAsync(
            OwnerRoute, new OwnerModuleEndpoints.GrantModuleRequest
            {
                ModuleKey = grantedKey,
                TriggerWords = ["/granted"],
                EntryPoint = "https://calendar.example.com",
                Credential = "an-owner-minted-secret-of-sixteen-plus-chars",
                ProvisioningSecret = "a-provisioning-secret-of-sixteen-plus-chars",
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            });
        Assert.Equal(HttpStatusCode.OK, grant.StatusCode);

        var modules = await GetModulesAsync(operatorClient);
        var purchasedRow = Assert.Single(modules.Modules, m => m.ModuleKey == purchasedKey);
        var grantedRow = Assert.Single(modules.Modules, m => m.ModuleKey == grantedKey);

        Assert.False(purchasedRow.GrantedByOwner);
        Assert.Null(purchasedRow.ExpiresAt);
        Assert.True(grantedRow.GrantedByOwner);
        Assert.NotNull(grantedRow.ExpiresAt);
    }

    /// <summary>The item's own second Done-when: revoking works and is proven by trying it, not
    /// asserted. Granted through the owner's own route, revoked through the owner's own route, with
    /// no operator credentials involved anywhere in this test - the platform owner does not borrow a
    /// tenant's own permission to undo what it granted.</summary>
    [Fact]
    public async Task OwnerToken_RevokesAGrant_AndTheTenantsOwnListingNoLongerShowsIt()
    {
        var moduleKey = UniqueModuleKey();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());
        var operatorClient = CreateClient(host, await fixture.GetDemoAdminAccessTokenAsync());

        await ownerClient.PutAsJsonAsync(
            OwnerRoute, new OwnerModuleEndpoints.GrantModuleRequest
            {
                ModuleKey = moduleKey,
                TriggerWords = ["/to-be-revoked"],
                EntryPoint = "https://calendar.example.com",
                Credential = "an-owner-minted-secret-of-sixteen-plus-chars",
                ProvisioningSecret = "a-provisioning-secret-of-sixteen-plus-chars",
                ExpiresAt = null,
            });

        var beforeRevoke = await GetModulesAsync(operatorClient);
        Assert.Contains(beforeRevoke.Modules, m => m.ModuleKey == moduleKey);

        var revokeResponse = await ownerClient.SendAsync(new HttpRequestMessage(
            HttpMethod.Delete, $"{OwnerRoute}/{moduleKey}")
        {
            Content = JsonContent.Create(
                new OwnerModuleEndpoints.RevokeModuleAsOwnerRequest("a-provisioning-secret-of-sixteen-plus-chars")),
        });
        Assert.Equal(HttpStatusCode.OK, revokeResponse.StatusCode);

        // Trying it: the entitlement really is gone, not merely reported gone.
        var afterRevoke = await GetModulesAsync(operatorClient);
        Assert.DoesNotContain(afterRevoke.Modules, m => m.ModuleKey == moduleKey);
    }

    /// <summary>An `ExpiresAt` in the past is refused before anything is granted - the same
    /// "decide, don't default" guard `EnableModuleForSiteAsOwnerHandler` enforces, reached this time
    /// through the real HTTP body.</summary>
    [Fact]
    public async Task OwnerToken_WithAnExpiryInThePast_IsRefused_AndGrantsNothing()
    {
        var moduleKey = UniqueModuleKey();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());
        var operatorClient = CreateClient(host, await fixture.GetDemoAdminAccessTokenAsync());

        var response = await ownerClient.PutAsJsonAsync(
            OwnerRoute, new OwnerModuleEndpoints.GrantModuleRequest
            {
                ModuleKey = moduleKey,
                TriggerWords = ["/expired"],
                EntryPoint = "https://calendar.example.com",
                Credential = "an-owner-minted-secret-of-sixteen-plus-chars",
                ProvisioningSecret = "a-provisioning-secret-of-sixteen-plus-chars",
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1),
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var modules = await GetModulesAsync(operatorClient);
        Assert.DoesNotContain(modules.Modules, m => m.ModuleKey == moduleKey);
    }

    // ------------------------------------------------------------------------------------------
    // The authorization boundary: this route is RequirePlatformOwner and nothing weaker.
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task OrdinaryOperatorToken_IsRejected()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, await fixture.GetDemoOperatorAccessTokenAsync());

        var response = await client.PutAsJsonAsync(
            OwnerRoute, new OwnerModuleEndpoints.GrantModuleRequest
            {
                ModuleKey = UniqueModuleKey(),
                TriggerWords = ["/x"],
                EntryPoint = "https://calendar.example.com",
                Credential = "a-perfectly-valid-shaped-secret-value-x",
                ProvisioningSecret = "a-provisioning-secret-of-sixteen-plus-chars",
                ExpiresAt = null,
            });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>The case that matters most, the identical lesson `OwnerSitesEndpointTests` already
    /// proves for the read side: `5-08`'s site-wide `"Admin"`, holding `site:configure` for their own
    /// site, is still not the platform owner. A permission granted broadly inside one tenant does not
    /// become a cross-tenant write.</summary>
    [Fact]
    public async Task SiteConfigureHoldingAdminToken_IsRejected()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, await fixture.GetDemoAdminAccessTokenAsync());

        var response = await client.PutAsJsonAsync(
            OwnerRoute, new OwnerModuleEndpoints.GrantModuleRequest
            {
                ModuleKey = UniqueModuleKey(),
                TriggerWords = ["/x"],
                EntryPoint = "https://calendar.example.com",
                Credential = "a-perfectly-valid-shaped-secret-value-x",
                ProvisioningSecret = "a-provisioning-secret-of-sixteen-plus-chars",
                ExpiresAt = null,
            });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NoToken_IsRejected()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token: null);

        var response = await client.PutAsJsonAsync(
            OwnerRoute, new OwnerModuleEndpoints.GrantModuleRequest
            {
                ModuleKey = UniqueModuleKey(),
                TriggerWords = ["/x"],
                EntryPoint = "https://calendar.example.com",
                Credential = "a-perfectly-valid-shaped-secret-value-x",
                ProvisioningSecret = "a-provisioning-secret-of-sixteen-plus-chars",
                ExpiresAt = null,
            });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>`22-17`'s own "must not become the normal path" claim, checked mechanically: a
    /// platform-owner token is refused on the tenant's own self-service route exactly as an ordinary
    /// operator token is refused on the owner's route - the two surfaces do not blur into each
    /// other.</summary>
    [Fact]
    public async Task OwnerToken_IsRejectedOnTheSelfServiceRoute()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await client.PutAsJsonAsync(
            SelfServiceRoute, new ModuleEndpoints.EnableModuleRequest(
                UniqueModuleKey(), ["/x"], "https://calendar.example.com",
                "a-perfectly-valid-shaped-secret-value-x", "a-provisioning-secret-of-sixteen-plus-chars"));

        // The platform owner has no `operators` row (`adr/0032`), so `RequireOperatorIdentity` never
        // even resolves an OperatorId claim for this token - the same 403 an unrecognised principal
        // gets, proven here against a token that is very much recognised, just not as this kind of
        // caller.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<ModuleEndpoints.EnabledModulesResponse> GetModulesAsync(HttpClient operatorClient)
    {
        var response = await operatorClient.GetAsync(SelfServiceRoute);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ModuleEndpoints.EnabledModulesResponse>();
        Assert.NotNull(body);
        return body;
    }

    private static string UniqueModuleKey() => $"owner-grant-{Guid.NewGuid():N}"[..24];

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
        builder.Services.AddScoped<ResolveOperatorIdentityHandler>();
        builder.Services.AddScoped<IPermissionChecker, PermissionChecker>();
        builder.Services.AddScoped<IEnabledModuleRepository, EnabledModuleRepository>();
        builder.Services.AddScoped<IEnabledModuleReadStore, EnabledModuleReadStore>();
        builder.Services.AddScoped<ISiteRepository, SiteRepository>();
        // A fake, not a real HTTP call - this suite is about the wire from operator/owner to
        // handler, not about whether a module deployment answers (the identical judgement
        // ModuleEndpointsTests's own remarks make for its sibling).
        builder.Services.AddSingleton<IModuleRegistrationGateway>(new AlwaysSucceedsModuleRegistrationGateway());
        builder.Services.AddSingleton<IClock, Ago.Platform.Hosting.SystemClock>();

        builder.Services.AddScoped<EnableModuleForSiteHandler>();
        builder.Services.AddScoped<RotateModuleCredentialHandler>();
        builder.Services.AddScoped<RevokeModuleForSiteHandler>();
        // `23-01`: MapModuleEndpoints maps the whole self-service group at once, including
        // the GET route's own handler - an unregistered handler here fails endpoint
        // construction for the group as a whole (ModuleEndpointsTests's own remarks), which
        // is exactly what this file's own fails-before run demonstrated.
        builder.Services.AddScoped<ListEnabledModulesForSiteHandler>();
        builder.Services.AddScoped<VerifyModuleRegistrationHandler>();
        builder.Services.AddScoped<EnableModuleForSiteAsOwnerHandler>();
        builder.Services.AddScoped<RevokeModuleForSiteAsOwnerHandler>();
        // `24-12`: the owner endpoint's own access-record write - OwnerAccessRecorder resolves this
        // straight from DI, the same way the production host does. IClock/IIdGenerator are already
        // registered above (AddPlatformKernel).
        builder.Services.AddScoped<IAccessRecordRepository, AccessRecordRepository>();

        builder.Services.AddHttpContextAccessor();
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
            // `Program.cs`'s own declarations, reproduced verbatim - both policies, on the same host,
            // because this file proves the boundary between them, not just one side of it.
            options.AddPolicy("RequireOperatorIdentity", policy => policy
                .AddAuthenticationSchemes(JwtSchemes.Operator)
                .RequireClaim(AgoClaimTypes.OperatorId));
            options.AddPolicy("RequirePlatformOwner", policy => policy
                .AddAuthenticationSchemes(JwtSchemes.Operator)
                .RequireAuthenticatedUser()
                .AddRequirements(new PlatformOwnerRequirement()));
        });
        builder.Services.AddSingleton<IAuthorizationHandler, PlatformOwnerAuthorizationHandler>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapModuleEndpoints();
        app.MapOwnerModuleEndpoints();

        await app.StartAsync();
        return app;
    }

    /// <summary>Always succeeds, recording nothing this suite needs - the identical fake-gateway
    /// shape <c>ModuleEndpointsTests</c>'s own private nested class already establishes for its
    /// sibling suite (not shared as a type, since each integration suite keeps its own minimal
    /// double rather than a shared test-only dependency).</summary>
    private sealed class AlwaysSucceedsModuleRegistrationGateway : IModuleRegistrationGateway
    {
        public Task RegisterAsync(
            ModuleRegistrationTarget module, ModuleCredential credential, ModuleProvisioningSecret provisioningSecret,
            string displayName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RotateAsync(
            ModuleRegistrationTarget module, ModuleCredential newCredential, ModuleProvisioningSecret provisioningSecret,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RevokeAsync(
            ModuleRegistrationTarget module, ModuleProvisioningSecret provisioningSecret, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<ModuleRegistrationRemoteStatus> GetStatusAsync(
            ModuleRegistrationTarget module, ModuleProvisioningSecret provisioningSecret, CancellationToken cancellationToken) =>
            Task.FromResult(new ModuleRegistrationRemoteStatus(Exists: true, DateTimeOffset.UtcNow, HasCredentialInGracePeriod: false));
    }
}
