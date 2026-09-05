using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Sites;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetMessageArchiveDownloadUrl;
using Ago.Chat.Application.UseCases.GetSiteExportStatus;
using Ago.Chat.Application.UseCases.ListMessageArchives;
using Ago.Chat.Application.UseCases.RegisterSite;
using Ago.Chat.Application.UseCases.RequestSiteExport;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
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
/// `13-07`/`adr/0068`'s own Done-when, against a real Keycloak and a real Postgres
/// (<see cref="OperatorOidcFixture"/>) - the header-driven half of the resolution algorithm
/// (<see cref="ActiveSiteHubResolutionTests"/> covers the hub's query-string half separately, since
/// that is a genuinely different transport question). Reuses <see cref="SiteRegistrationTests"/>'s own
/// real-<c>WebApplication</c>-over-<c>TestServer</c> technique, extended with one test-only route
/// (`/me/site`) that echoes the resolved <c>OperatorId</c>/<c>SiteId</c> claims back, so a test can
/// assert on the exact pair a request resolved to rather than only on a status code.
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class ActiveSiteResolutionTests(OperatorOidcFixture fixture)
{
    /// <summary>
    /// Done-when: "A multi-tenant identity switching the header between its two `Site`s gets the
    /// correct, distinct `SiteId`/`OperatorId` claim pair each time, proven against a real
    /// `RequireOperatorIdentity`-gated route... not asserted from the handler alone." Two real
    /// registrations, two real headers, two real round trips.
    /// </summary>
    [Fact]
    public async Task ARequestCarryingTheHeaderForOneOfTwoRealTenancies_ResolvesTheCorrectDistinctSiteAndOperatorEachTime()
    {
        var (token, _) = await fixture.CreateFreshUserAccessTokenAsync();

        await using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var siteA = await RegisterSiteAsync(client, "Acme Support");
        var siteB = await RegisterSiteAsync(client, "Acme Support - Second Shop");
        Assert.NotEqual(siteA.SiteId, siteB.SiteId);

        var resolvedA = await GetMySiteAsync(client, siteA.SiteId);
        Assert.Equal(HttpStatusCode.OK, resolvedA.Status);
        Assert.Equal(siteA.SiteId, resolvedA.Body!.SiteId);
        Assert.Equal(siteA.OperatorId, resolvedA.Body.OperatorId);

        var resolvedB = await GetMySiteAsync(client, siteB.SiteId);
        Assert.Equal(HttpStatusCode.OK, resolvedB.Status);
        Assert.Equal(siteB.SiteId, resolvedB.Body!.SiteId);
        Assert.Equal(siteB.OperatorId, resolvedB.Body.OperatorId);

        // The identical request, only the header changed - and it resolved to a genuinely different
        // operator, not the same one twice.
        Assert.NotEqual(resolvedA.Body.OperatorId, resolvedB.Body.OperatorId);
    }

    /// <summary>
    /// The tenant-isolation-critical case, `adr/0068`'s own "never misdirect" invariant proven over
    /// the wire: a real second identity really does administer <c>siteB</c>, and the first identity's
    /// own, otherwise-valid token asks (via the client-controlled header) for that site specifically.
    /// A wrong implementation that fell back to the caller's own real tenancy would return `200` here;
    /// this asserts the actual refusal, and that the site never leaked into a resolved claim.
    /// </summary>
    [Fact]
    public async Task ARequestCarryingTheHeaderForASiteThisIdentityDoesNotAdminister_IsRefused_NeverFallsBackToItsOwnSite()
    {
        var (tokenA, _) = await fixture.CreateFreshUserAccessTokenAsync();
        var (tokenB, _) = await fixture.CreateFreshUserAccessTokenAsync();

        await using var host = await BuildTestHostAsync();

        using var clientA = host.GetTestClient();
        clientA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
        var siteA = await RegisterSiteAsync(clientA, "Acme Support");

        using var clientB = host.GetTestClient();
        clientB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);
        var siteB = await RegisterSiteAsync(clientB, "Widgets Inc");

        // Identity A's own real, valid token - asking for identity B's real site.
        var crossTenantAttempt = await GetMySiteAsync(clientA, siteB.SiteId);

        Assert.Equal(HttpStatusCode.Forbidden, crossTenantAttempt.Status);

        // And identity A's own site still resolves correctly on its own - the refusal above was not a
        // side effect that broke resolution generally.
        var stillWorks = await GetMySiteAsync(clientA, siteA.SiteId);
        Assert.Equal(HttpStatusCode.OK, stillWorks.Status);
        Assert.Equal(siteA.SiteId, stillWorks.Body!.SiteId);
    }

    /// <summary>
    /// Done-when: "A pre-existing, single-tenant identity... resolves identically with the header
    /// absent as it did before this item - a real regression test proving zero behavioural change."
    /// <see cref="OperatorOidcFixture.DemoOperatorUsername"/> is exactly that identity - seeded once,
    /// shared, single-tenant, and older than this item.
    /// </summary>
    [Fact]
    public async Task APreExistingSingleTenantIdentity_ResolvesIdenticallyWithTheHeaderAbsent()
    {
        var token = await fixture.GetDemoOperatorAccessTokenAsync();

        await using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resolved = await GetMySiteAsync(client, requestedSiteId: null);

        Assert.Equal(HttpStatusCode.OK, resolved.Status);
        Assert.Equal(fixture.SeededSiteId.Value, resolved.Body!.SiteId);
        Assert.Equal(fixture.SeededOperatorId.Value, resolved.Body.OperatorId);
    }

    private static async Task<(Guid SiteId, Guid OperatorId)> RegisterSiteAsync(HttpClient client, string siteName)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/sites", new SitesEndpoints.RegisterSiteRequest(siteName, $"https://{Guid.NewGuid():N}.example.com"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SitesEndpoints.RegisterSiteResponse>();
        Assert.NotNull(body);
        return (body.SiteId, body.OperatorId);
    }

    private static async Task<(HttpStatusCode Status, MySiteResponse? Body)> GetMySiteAsync(HttpClient client, Guid? requestedSiteId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/me/site");
        if (requestedSiteId is { } siteId)
        {
            request.Headers.Add(OperatorIdentityClaimsTransformation.ActiveSiteHeaderName, siteId.ToString());
        }

        var response = await client.SendAsync(request);
        var body = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<MySiteResponse>() : null;
        return (response.StatusCode, body);
    }

    private sealed record MySiteResponse(Guid OperatorId, Guid SiteId);

    /// <summary>Same real-<c>WebApplication</c>-over-<c>TestServer</c> shape as
    /// <see cref="SiteRegistrationTests.BuildTestHostAsync"/> (that method's own remarks explain the
    /// choice), extended with `/me/site` - a test-only route with no production equivalent, echoing
    /// the resolved claims so a test can assert the exact pair rather than only a status code.</summary>
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
        builder.Services.AddScoped<ResolveOperatorIdentityHandler>();
        builder.Services.AddScoped<RegisterSiteHandler>();
        // `16-03`: SitesEndpoints now also maps the export routes - see SiteRegistrationTests'
        // own remarks (this file's own precedent for a stripped-down host).
        builder.Services.AddScoped<IExportRequestRepository, ExportRequestRepository>();
        builder.Services.AddScoped<IPermissionChecker, PermissionChecker>();
        builder.Services.AddSingleton<IFileStorage, FakeFileStorage>();
        builder.Services.AddSingleton(new SiteExportRateLimitOptions());
        builder.Services.AddSingleton(new SiteExportOptions());
        builder.Services.AddScoped<RequestSiteExportHandler>();
        builder.Services.AddScoped<GetSiteExportStatusHandler>();
        // `13-06`: SitesEndpoints now also maps the message-archive retrieval routes - the same
        // "every mapped route's handler dependencies must resolve, even one this test never calls"
        // reason FakeFileStorage's own remarks give for IFileStorage above.
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
        app.MapGet("/me/site", (HttpContext context) => Results.Ok(
                new MySiteResponse(context.User.GetOperatorId().Value, context.User.GetSiteId().Value)))
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = JwtSchemes.Operator,
                Policy = "RequireOperatorIdentity",
            });

        await app.StartAsync();
        return app;
    }
}
