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
            Route, new ModuleEndpoints.EnableModuleRequest("faq", ["/faq"], "https://faq.example.com", "a-shared-secret-of-sixteen-plus-chars"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ModuleEndpoints.EnableModuleResponse>();
        Assert.NotNull(body);
        Assert.Equal("faq", body.ModuleKey);

        var listResponse = await client.GetAsync(Route);
        var list = await listResponse.Content.ReadFromJsonAsync<ModuleEndpoints.EnabledModulesResponse>();
        Assert.NotNull(list);
        Assert.Contains(list.Modules, m => m.ModuleKey == "faq");
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
            Route, new ModuleEndpoints.EnableModuleRequest("faq", ["/faq"], "https://faq.example.com", "a-shared-secret-of-sixteen-plus-chars"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NoToken_IsRejected()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token: null);

        var response = await client.PutAsJsonAsync(
            Route, new ModuleEndpoints.EnableModuleRequest("faq", ["/faq"], "https://faq.example.com", "a-shared-secret-of-sixteen-plus-chars"));

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
        builder.Services.AddScoped<EnableModuleForSiteHandler>();

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
}
