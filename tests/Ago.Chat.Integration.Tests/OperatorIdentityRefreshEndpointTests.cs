using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Operators;
using Ago.Chat.Api.Sites;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetMessageArchiveDownloadUrl;
using Ago.Chat.Application.UseCases.GetMyPermissions;
using Ago.Chat.Application.UseCases.GetOperatorTeam;
using Ago.Chat.Application.UseCases.GetSiteConfigById;
using Ago.Chat.Application.UseCases.GetSiteExportStatus;
using Ago.Chat.Application.UseCases.ListMessageArchives;
using Ago.Chat.Application.UseCases.GetSeatAssignmentSummary;
using Ago.Chat.Application.UseCases.RegisterSite;
using Ago.Chat.Application.UseCases.RemoveOperator;
using Ago.Chat.Application.UseCases.RequestSiteExport;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Application.UseCases.ToggleOperatorSeat;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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
/// `23-02`'s own Done-when, against a real Keycloak and a real Postgres - the sign-in refresh
/// `decisions.md` §1 requires, exercised the one way it is actually observable server-side:
/// `GET /api/v1/operators/me`, which `GetMyPermissionsHandler` now also uses to rewrite the caller's
/// `operators.display_name`/`email` on every call. `OperatorInviteEndpointTests.
/// Redeem_ARealTokenCarryingNameAndEmailClaims_EndsWithBothOnTheOperatorRow` covers the *capture* half
/// of this item's Done-when; this file covers the *refresh* half.
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class OperatorIdentityRefreshEndpointTests(OperatorOidcFixture fixture)
{
    /// <summary>The item's own Done-when, word for word: "changing the name in the IdP and signing in
    /// again updates the row - a test calling `GET /api/v1/operators/me` twice with two different
    /// `name` claims, asserting one row and two values." The first call's own claims already match
    /// what `RegisterSiteHandler` wrote at registration (`CreateFreshUserAccessTokenAsync`'s own
    /// `firstName: "Self", lastName: "Register"`), so it is the *second* call's write this test's own
    /// name is about - the first is here only to establish the starting value a change can be measured
    /// against.</summary>
    [Fact]
    public async Task GetMe_CalledTwiceWithDifferentNameClaims_UpdatesTheSameRowToTheSecondValue()
    {
        await using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();

        var (token, username) = await fixture.CreateFreshUserAccessTokenAsync();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var registerResponse = await client.PostAsJsonAsync(
            "/api/v1/sites", new SitesEndpoints.RegisterSiteRequest("Acme Support", "https://shop.example.com"));
        Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);
        var registered = await registerResponse.Content.ReadFromJsonAsync<SitesEndpoints.RegisterSiteResponse>();
        Assert.NotNull(registered);

        using var firstCall = host.GetTestClient();
        firstCall.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var firstResponse = await firstCall.GetAsync("/api/v1/operators/me");
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var first = await firstResponse.Content.ReadFromJsonAsync<OperatorPermissionsResponseForTest>();
        Assert.NotNull(first);
        Assert.Equal("Self Register", first.DisplayName);
        // `23-21`: this response's other field, on the same wire - a rebase onto that item must not
        // silently drop it back off the response.
        Assert.NotNull(first.EnabledModules);

        // The IdP-side change - a real update, over Keycloak's own admin API, to the identity's
        // firstName/lastName. The *current* token is unaffected (already issued); only a token minted
        // afterward carries the new `name` claim, which is why a fresh one is minted below rather than
        // reusing `token`.
        await fixture.UpdateUserNameAsync(username, "Ivan", "Petrov");
        var secondToken = await fixture.RefreshAccessTokenAsync(username);

        using var secondCall = host.GetTestClient();
        secondCall.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secondToken);
        var secondResponse = await secondCall.GetAsync("/api/v1/operators/me");
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var second = await secondResponse.Content.ReadFromJsonAsync<OperatorPermissionsResponseForTest>();
        Assert.NotNull(second);
        Assert.Equal("Ivan Petrov", second.DisplayName);
        Assert.NotNull(second.EnabledModules);

        // One row, two values over time - never a second row for the same identity.
        await using var db = fixture.CreateDbContext();
        var operatorRows = await db.Operators
            .Where(o => o.SiteId == new SiteId(registered.SiteId))
            .ToListAsync();
        var operatorRow = Assert.Single(operatorRows);
        Assert.Equal("Ivan Petrov", operatorRow.DisplayName);
    }

    /// <summary>The DTO shape this test needs from `OperatorPermissionsResponse` - a local record
    /// rather than a reference to `Ago.Chat.Contracts` internals this project already references
    /// directly; kept here because no other test in this project currently deserializes this
    /// response and a one-off local shape is cheaper than exporting a shared one for a single
    /// caller.</summary>
    private sealed record OperatorPermissionsResponseForTest(
        Guid OperatorId, Guid SiteId, IReadOnlyList<string> Permissions, string Locale,
        IReadOnlyList<string> EnabledModules, string? DisplayName);

    private async Task<WebApplication> BuildTestHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddRouting();
        builder.Services.AddSingleton(fixture.DataSource);
        builder.Services.AddDbContext<AgoChatDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<Npgsql.NpgsqlDataSource>()));
        builder.Services.AddScoped<IOperatorRepository, OperatorRepository>();
        builder.Services.AddScoped<ISiteRegistrationRepository, SiteRegistrationRepository>();
        builder.Services.AddScoped<IOutboxWriter, EfOutboxWriter<AgoChatDbContext>>();
        // `24-03`: RegisterSiteHandler's own two new dependencies - SiteRegistrationTests' own
        // remarks on why every handler this host maps must resolve from its own container.
        builder.Services.AddScoped<IRequiredDocumentRepository, RequiredDocumentRepository>();
        builder.Services.AddScoped<IDocumentRepository, DocumentRepository>();
        builder.Services.AddScoped<IPermissionChecker, PermissionChecker>();
        builder.Services.AddScoped<ISiteRepository, SiteRepository>();
        builder.Services.AddSingleton<ICache, NoOpCache>();
        builder.Services.AddScoped<ResolveOperatorIdentityHandler>();
        builder.Services.AddScoped<RegisterSiteHandler>();
        builder.Services.AddScoped<GetSiteConfigByIdHandler>();
        builder.Services.AddScoped<IEnabledModuleReadStore, EnabledModuleReadStore>();
        builder.Services.AddScoped<GetMyPermissionsHandler>();
        // `OperatorsEndpoints.MapOperatorsEndpoints()` maps three more routes beyond `operators/me` -
        // the identical "every mapped endpoint's handler must be a registered service, found live the
        // same way" as the `MapSitesEndpoints()` note right below.
        builder.Services.AddScoped<ToggleOperatorSeatHandler>();
        builder.Services.AddScoped<RemoveOperatorHandler>();
        builder.Services.AddScoped<GetSeatAssignmentSummaryHandler>();
        // `23-22`: the fourth route `MapOperatorsEndpoints()` now maps - the identical "every mapped
        // endpoint's handler must be a registered service" requirement the three lines above already
        // exist to satisfy.
        builder.Services.AddScoped<IOperatorTeamReadStore, OperatorTeamReadStore>();
        builder.Services.AddScoped<GetOperatorTeamHandler>();
        // `SitesEndpoints.MapSitesEndpoints()` also maps the erasure/export/archive routes - ASP.NET
        // Core builds every mapped endpoint's metadata eagerly (`EndpointDataSource.Endpoints`), so a
        // service missing for *any* of them fails host startup, not only a request to that route. The
        // exact registrations `OperatorInviteEndpointTests.BuildTestHostAsync` already needed for the
        // identical `MapSitesEndpoints()` call, found live the same way that file's own comments
        // describe finding theirs.
        builder.Services.AddScoped<IExportRequestRepository, ExportRequestRepository>();
        builder.Services.AddSingleton<IFileStorage, FakeFileStorage>();
        builder.Services.AddSingleton(new SiteExportRateLimitOptions());
        builder.Services.AddSingleton(new SiteExportOptions());
        builder.Services.AddScoped<RequestSiteExportHandler>();
        builder.Services.AddScoped<GetSiteExportStatusHandler>();
        builder.Services.AddSingleton<IMessageArchiveRepository, MessageArchiveRepository>();
        builder.Services.AddSingleton(new MessageArchiveOptions());
        builder.Services.AddScoped<ListMessageArchivesHandler>();
        builder.Services.AddScoped<GetMessageArchiveDownloadUrlHandler>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IClaimsTransformation, OperatorIdentityClaimsTransformation>();
        builder.Services.AddSingleton<IRateLimiter, FakeRateLimiter>();
        builder.Services.AddSingleton(new RegisterSiteRateLimitOptions());
        builder.Services.AddSingleton<IIdGenerator, UuidV7Generator>();
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
            options.AddPolicy(
                "RequireOperatorIdentity",
                policy => policy.AddAuthenticationSchemes(JwtSchemes.Operator).RequireClaim(AgoClaimTypes.OperatorId));
            options.AddPolicy(
                "RequireKeycloakIdentity",
                policy => policy.AddAuthenticationSchemes(JwtSchemes.Operator).RequireAuthenticatedUser());
        });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapSitesEndpoints();
        app.MapOperatorsEndpoints();

        await app.StartAsync();
        return app;
    }
}
