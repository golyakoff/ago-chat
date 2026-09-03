using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Modules;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.EnableModuleForSite;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
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
/// `19-03`: `PUT/GET /api/v1/sites/{siteId}/modules` - the HTTP surface `20-07` left unbuilt
/// (`EnableModuleForSite`'s own doc comment). This proves the wiring `ModuleEndpoints` adds:
/// `"RequireOperatorIdentity"` policy, route-level `siteId`, and the handler's own permission check
/// reached through a real token - the same minimal-host technique <see cref="OwnerSitesEndpointTests"/>
/// established, scaled down to this route's own (smaller) dependency set rather than the owner
/// surface's. The handler's own business rules (trigger-word conflicts, reserved words, invalid
/// module keys) are already fully proven at the Application level
/// (<c>EnableModuleForSiteHandlerTests</c>) - this file's job is only "does a real HTTP call, with a
/// real token, reach that handler and come back with the right status", not to re-prove logic that
/// unit tests already cover more cheaply.
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class ModuleEndpointsTests(OperatorOidcFixture fixture)
{
    private string Route => $"/api/v1/sites/{fixture.SeededSiteId.Value}/modules";

    [Fact]
    public async Task AdminToken_HoldingSiteConfigure_RegistersTheModule()
    {
        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var response = await client.PutAsJsonAsync(
            Route, new ModuleEndpoints.EnableModuleRequest(
                "faq", ["/faq"], "https://faq.example.com", "a-shared-secret-of-sixteen-plus-chars",
                "a-provisioning-secret-of-sixteen-plus-chars"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ModuleEndpoints.EnableModuleResponse>();
        Assert.NotNull(body);
        Assert.Equal("faq", body.ModuleKey);

        var listResponse = await client.GetAsync(Route);
        var list = await listResponse.Content.ReadFromJsonAsync<ModuleEndpoints.EnabledModulesResponse>();
        Assert.NotNull(list);
        Assert.Contains(list.Modules, m => m.ModuleKey == "faq");
    }

    /// <summary>`22-11`: the rotate route, over the real HTTP pipeline - the fresh credential this
    /// (fake) module confirms comes back in the response, the one place this codebase ever echoes a
    /// credential it minted itself.</summary>
    [Fact]
    public async Task AdminToken_HoldingSiteConfigure_RotatesTheCredential()
    {
        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);
        // A module key unique to this test - the shared OperatorOidcFixture seeds one site for the
        // whole collection, and EnableModuleForSiteHandler's own documented limitation (no
        // one-row-per-(site,module) uniqueness yet) means re-registering "faq" across sibling tests
        // in this file would accumulate rows rather than update one, which is exactly what happened
        // here on the first pass - found running, not by inspection.
        await client.PutAsJsonAsync(
            Route, new ModuleEndpoints.EnableModuleRequest(
                "faq-rotate-test", ["/faq-rotate-test"], "https://faq.example.com", "a-shared-secret-of-sixteen-plus-chars",
                "a-provisioning-secret-of-sixteen-plus-chars"));

        var response = await client.PostAsJsonAsync(
            $"{Route}/faq-rotate-test/rotate", new ModuleEndpoints.RotateModuleCredentialRequest("a-provisioning-secret-of-sixteen-plus-chars"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ModuleEndpoints.RotateModuleCredentialResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body.NewCredential));
    }

    /// <summary>`22-11`'s own third Done-when, over the real HTTP pipeline: revoking removes the row,
    /// proven by the follow-up GET no longer listing the module.</summary>
    [Fact]
    public async Task AdminToken_HoldingSiteConfigure_RevokesTheModule()
    {
        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);
        // A module key unique to this test - see AdminToken_HoldingSiteConfigure_RotatesTheCredential's
        // own remarks on why sibling tests in this file must not share one.
        await client.PutAsJsonAsync(
            Route, new ModuleEndpoints.EnableModuleRequest(
                "faq-revoke-test", ["/faq-revoke-test"], "https://faq.example.com", "a-shared-secret-of-sixteen-plus-chars",
                "a-provisioning-secret-of-sixteen-plus-chars"));

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{Route}/faq-revoke-test")
        {
            Content = JsonContent.Create(new ModuleEndpoints.RevokeModuleRequest("a-provisioning-secret-of-sixteen-plus-chars")),
        };
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var listResponse = await client.GetAsync(Route);
        var list = await listResponse.Content.ReadFromJsonAsync<ModuleEndpoints.EnabledModulesResponse>();
        Assert.DoesNotContain(list!.Modules, m => m.ModuleKey == "faq-revoke-test");
    }

    /// <summary>`22-11`'s own fourth Done-when's operator-facing surface, over the real HTTP
    /// pipeline.</summary>
    [Fact]
    public async Task AdminToken_HoldingSiteConfigure_VerifiesTheRegistration()
    {
        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);
        // A module key unique to this test - see AdminToken_HoldingSiteConfigure_RotatesTheCredential's
        // own remarks on why sibling tests in this file must not share one.
        await client.PutAsJsonAsync(
            Route, new ModuleEndpoints.EnableModuleRequest(
                "faq-verify-test", ["/faq-verify-test"], "https://faq.example.com", "a-shared-secret-of-sixteen-plus-chars",
                "a-provisioning-secret-of-sixteen-plus-chars"));

        var response = await client.PostAsJsonAsync(
            $"{Route}/faq-verify-test/verify",
            new ModuleEndpoints.VerifyModuleRegistrationRequest("https://faq.example.com", "a-provisioning-secret-of-sixteen-plus-chars"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ModuleEndpoints.VerifyModuleRegistrationResponse>();
        Assert.NotNull(body);
        Assert.True(body.ChatHasRegistration);
    }

    /// <summary>An ordinary operator - authenticated, but holding no `site:configure` on this site -
    /// is refused by the handler's own permission check, reached through the real HTTP pipeline.</summary>
    [Fact]
    public async Task DemoOperatorToken_WithoutSiteConfigure_IsForbidden()
    {
        var token = await fixture.GetDemoOperatorAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var response = await client.PutAsJsonAsync(
            Route, new ModuleEndpoints.EnableModuleRequest(
                "faq", ["/faq"], "https://faq.example.com", "a-shared-secret-of-sixteen-plus-chars",
                "a-provisioning-secret-of-sixteen-plus-chars"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NoToken_IsRejected()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token: null);

        var response = await client.PutAsJsonAsync(
            Route, new ModuleEndpoints.EnableModuleRequest(
                "faq", ["/faq"], "https://faq.example.com", "a-shared-secret-of-sixteen-plus-chars",
                "a-provisioning-secret-of-sixteen-plus-chars"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
        builder.Services.AddPlatformKernel();
        builder.Services.AddSingleton(fixture.DataSource);
        builder.Services.AddDbContext<AgoChatDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<Npgsql.NpgsqlDataSource>()));

        // The production registrations for this route, exactly as ChatModule/AddPostgresPersistence
        // make them.
        builder.Services.AddScoped<IOperatorRepository, OperatorRepository>();
        builder.Services.AddScoped<ResolveOperatorIdentityHandler>();
        builder.Services.AddScoped<IPermissionChecker, PermissionChecker>();
        builder.Services.AddScoped<IEnabledModuleRepository, EnabledModuleRepository>();
        builder.Services.AddScoped<IEnabledModuleReadStore, EnabledModuleReadStore>();
        // `22-11`: a fake, not a real HTTP call - this suite is about the wire from operator to
        // handler (RequireOperatorIdentity, route-level siteId, the permission check), not about
        // whether the module deployment answers. The real module-provisioning HTTP round trip is
        // ModuleRegistrationEndpointTests's own job, in ago-calendar/ago-faq.
        builder.Services.AddSingleton<IModuleRegistrationGateway>(new FakeModuleRegistrationGateway());
        builder.Services.AddSingleton<IModuleCredentialGenerator, FixedModuleCredentialGenerator>();
        builder.Services.AddScoped<EnableModuleForSiteHandler>();
        // `22-11`: the route group's other three endpoints - registered here for the identical reason
        // EnableModuleForSiteHandler is: MapModuleEndpoints maps the whole group at once, so route
        // metadata for every endpoint in it is built together, and an unregistered handler for any one
        // of them fails endpoint construction for the group as a whole, not just its own route.
        builder.Services.AddScoped<Application.UseCases.RotateModuleCredential.RotateModuleCredentialHandler>();
        builder.Services.AddScoped<Application.UseCases.RevokeModuleForSite.RevokeModuleForSiteHandler>();
        builder.Services.AddScoped<Application.UseCases.VerifyModuleRegistration.VerifyModuleRegistrationHandler>();

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
            // `Program.cs`'s own declaration, reproduced verbatim.
            options.AddPolicy("RequireOperatorIdentity", policy => policy
                .AddAuthenticationSchemes(JwtSchemes.Operator)
                .RequireClaim(AgoClaimTypes.OperatorId));
        });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        // The real production mapping - no duplicated route or policy decision.
        app.MapModuleEndpoints();

        await app.StartAsync();
        return app;
    }

    /// <summary>Always succeeds, recording nothing this suite needs - the fake-gateway shape
    /// <c>Ago.Chat.Application.Tests.Fakes.FakeModuleGateway</c> already establishes for
    /// <see cref="IModuleGateway"/>'s own sibling.</summary>
    private sealed class FakeModuleRegistrationGateway : IModuleRegistrationGateway
    {
        public Task RegisterAsync(
            ModuleRegistrationTarget module, ModuleCredential credential, ModuleProvisioningSecret provisioningSecret,
            CancellationToken cancellationToken) => Task.CompletedTask;

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

    private sealed class FixedModuleCredentialGenerator : IModuleCredentialGenerator
    {
        public string NewCredential() => "a-fixed-test-credential-of-sixteen-plus-x";
    }
}
