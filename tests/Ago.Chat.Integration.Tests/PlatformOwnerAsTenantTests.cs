using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Owner;
using Ago.Chat.Api.Sites;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetMessageArchiveDownloadUrl;
using Ago.Chat.Application.UseCases.GetSiteExportStatus;
using Ago.Chat.Application.UseCases.ListMessageArchives;
using Ago.Chat.Application.UseCases.ListSitesForOwner;
using Ago.Chat.Application.UseCases.RegisterSite;
using Ago.Chat.Application.UseCases.RequestSiteExport;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Contracts;
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
/// `12-05`: **one identity is the platform owner and a tenant of its own, and both halves keep
/// working.**
///
/// <para>`adr/0063` concluded that platform owner and operator are orthogonal axes rather than
/// alternatives, and `12-04` then made them mutually exclusive at exactly one endpoint - a
/// `NotThePlatformOwner` policy on `POST /api/v1/sites`. `12-05` removes that refusal, and this file
/// is the evidence that removing it is safe: nothing else in the request pipeline was quietly
/// depending on the owner having no `operators` row.</para>
///
/// <para><b>The silent failure this file exists to rule out.</b> Before `12-05` the owner's token
/// carried no `operator_id` and no `site_id`, because
/// <see cref="OperatorIdentityClaimsTransformation"/> resolves nothing for a `sub` with no
/// `operators` row. Give that identity a tenant and the transformation starts resolving, so *every*
/// request it makes - `GET /api/v1/owner/sites` included - now arrives carrying a `site_id`. If any
/// part of that read had narrowed itself to the caller's own tenant, the owner would see a **shorter
/// list, not an error**, and a shorter list of tenants is indistinguishable from a platform with
/// fewer tenants. Reading `ListSitesForOwnerHandler` and `PlatformOverviewReadStore` says they do not
/// consult a claim; this asserts it against a token that genuinely holds both, which is the only form
/// of the claim that can fail.</para>
///
/// <para>Runs the real <c>SitesEndpoints.MapSitesEndpoints</c> and
/// <c>OwnerSitesEndpoints.MapOwnerEndpoints</c> in one <see cref="TestServer"/> - deliberately
/// together, because the point is a single identity crossing from one to the other, which two
/// separate hosts could not show. <see cref="SiteRegistrationTests"/> and
/// <see cref="OwnerSitesEndpointTests"/> keep owning each endpoint's own behaviour.</para>
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class PlatformOwnerAsTenantTests(OperatorOidcFixture fixture)
{
    private const string OwnerRoute = "/api/v1/owner/sites";
    private const string RegisterRoute = "/api/v1/sites";

    /// <summary>`12-05`'s first Done-when: the platform owner registers a site through the ordinary
    /// flow - the same endpoint, the same request body, the same `201` a self-registering shop gets -
    /// and afterwards genuinely holds both. The rows are read back from the database rather than
    /// inferred from the status code, because "refused before the transaction" and "committed" are
    /// what `12-04` and `12-05` actually differ on.</summary>
    [Fact]
    public async Task ThePlatformOwner_MayRegisterASite_ThroughTheOrdinaryEndpoint()
    {
        var (token, username) = await fixture.CreateFreshPlatformOwnerAccessTokenAsync();
        var externalSubjectId = ReadSubjectClaim(token);

        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var response = await client.PostAsJsonAsync(
            RegisterRoute, new SitesEndpoints.RegisterSiteRequest("Owner's Own Shop", "https://owner-shop.example.com"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SitesEndpoints.RegisterSiteResponse>();
        Assert.NotNull(body);

        await using var db = fixture.CreateDbContext();
        var operatorRow = await db.Operators.SingleAsync(o => o.ExternalSubjectId == externalSubjectId);
        Assert.Equal(new SiteId(body.SiteId), operatorRow.SiteId);

        // The other axis, still held: the identity that just became an operator is the same one
        // `RequirePlatformOwner` accepts. Asserted through the policy rather than by decoding the
        // token here, since the policy is what actually decides (`12-01`).
        using var ownerClient = CreateClient(host, await fixture.RefreshAccessTokenAsync(username));
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.GetAsync($"{OwnerRoute}?limit=1")).StatusCode);
    }

    /// <summary>`12-05`'s third Done-when, and the one that would fail silently: the cross-tenant read
    /// is not narrowed by the `site_id` the caller now carries.
    ///
    /// <para>The two assertions are not interchangeable. That the response contains the owner's *own*
    /// new site only proves the endpoint answered. That it also contains
    /// <see cref="OperatorOidcFixture.SeededSiteId"/> - a tenant this identity has no `operators` row
    /// in, seeded by the fixture before this identity existed - is the cross-tenant claim itself, and
    /// is what turns red if the read is ever scoped to the token's tenant.</para></summary>
    [Fact]
    public async Task AnIdentityHoldingBoth_StillSeesEveryTenant_NotOnlyItsOwn()
    {
        var (registrationToken, username) = await fixture.CreateFreshPlatformOwnerAccessTokenAsync();

        await using var host = await BuildTestHostAsync();
        Guid ownSiteId;
        using (var registrationClient = CreateClient(host, registrationToken))
        {
            var created = await registrationClient.PostAsJsonAsync(
                RegisterRoute, new SitesEndpoints.RegisterSiteRequest("Owner's Own Shop", "https://owner-shop.example.com"));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            ownSiteId = (await created.Content.ReadFromJsonAsync<SitesEndpoints.RegisterSiteResponse>())!.SiteId;
        }

        // A *second* token for the same identity, minted after the `operators` row exists. Not
        // strictly required - `OperatorIdentityClaimsTransformation` resolves per request, not per
        // token - but it removes the only remaining way a reader could dismiss the result as an
        // artifact of a token issued before the tenant existed.
        using var client = CreateClient(host, await fixture.RefreshAccessTokenAsync(username));

        // The precondition, established rather than assumed: this identity's requests really do carry
        // a `site_id` now. Without this the test below could pass for the uninteresting reason that
        // nothing changed at all.
        var carriedSiteId = await client.GetStringAsync("/carried-site-id");
        Assert.Equal(ownSiteId.ToString(), carriedSiteId);

        var seen = await ReadEverySiteIdAsync(client);

        Assert.Contains(ownSiteId, seen);
        Assert.Contains(fixture.SeededSiteId.Value, seen);
    }

    /// <summary>Walks every keyset page, because "returns every tenant" is a claim about the whole
    /// result and not about the first page of it - and because this collection's database is shared,
    /// so how many tenants exist by the time this runs is not something one page can bound.</summary>
    private static async Task<List<Guid>> ReadEverySiteIdAsync(HttpClient client)
    {
        var seen = new List<Guid>();
        Guid? cursor = null;
        do
        {
            var query = cursor is { } before ? $"?before={before}&limit=50" : "?limit=50";
            var response = await client.GetAsync($"{OwnerRoute}{query}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var page = await response.Content.ReadFromJsonAsync<OwnerSitesResponse>();
            Assert.NotNull(page);
            seen.AddRange(page.Sites.Select(s => s.SiteId));
            cursor = page.NextBefore;
        } while (cursor is not null);

        return seen;
    }

    private static string ReadSubjectClaim(string jwt) => new JwtSecurityTokenHandler().ReadJwtToken(jwt).Subject;

    private static HttpClient CreateClient(WebApplication host, string token)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Both production mappings in one host, plus one probe route of this file's own
    /// (`/carried-site-id`) that exists only to read back the claim
    /// <see cref="OperatorIdentityClaimsTransformation"/> added - the fact the whole item turns on,
    /// and one that no production endpoint exposes.</summary>
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
        builder.Services.AddScoped<IPlatformOverviewReadStore, PlatformOverviewReadStore>();
        builder.Services.AddScoped<ListSitesForOwnerHandler>();
        // `24-12`: the owner endpoint's own access-record write - OwnerAccessRecorder resolves this
        // straight from DI, the same way the production host does. IClock/IIdGenerator are already
        // registered below.
        builder.Services.AddScoped<IAccessRecordRepository, AccessRecordRepository>();
        // `16-03`: SitesEndpoints now also maps the export routes - see SiteRegistrationTests'
        // own remarks (this file's own precedent for a stripped-down host).
        builder.Services.AddScoped<IExportRequestRepository, ExportRequestRepository>();
        builder.Services.AddScoped<IPermissionChecker, PermissionChecker>();
        builder.Services.AddSingleton<IFileStorage, FakeFileStorage>();
        builder.Services.AddSingleton(new SiteExportRateLimitOptions());
        builder.Services.AddSingleton(new SiteExportOptions());
        builder.Services.AddScoped<RequestSiteExportHandler>();
        builder.Services.AddScoped<GetSiteExportStatusHandler>();
        // `13-06`: SitesEndpoints now also maps the message-archive retrieval routes - same reasoning
        // as the export registrations right above.
        builder.Services.AddSingleton<IMessageArchiveRepository, MessageArchiveRepository>();
        builder.Services.AddSingleton(new MessageArchiveOptions());
        builder.Services.AddScoped<ListMessageArchivesHandler>();
        builder.Services.AddScoped<GetMessageArchiveDownloadUrlHandler>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IClaimsTransformation, OperatorIdentityClaimsTransformation>();
        builder.Services.AddSingleton<IRateLimiter, FakeRateLimiter>();
        builder.Services.AddSingleton(new RegisterSiteRateLimitOptions());
        builder.Services.AddSingleton<IIdGenerator, UuidV7Generator>();
        // Fully qualified: `Microsoft.AspNetCore.Authentication.SystemClock` is also in scope here,
        // exactly as in every other TestServer file in this suite.
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
            // `Program.cs`'s own declarations, reproduced verbatim - the same hand transcription
            // every TestServer file in this suite makes.
            options.AddPolicy(
                "RequireKeycloakIdentity",
                policy => policy.AddAuthenticationSchemes(JwtSchemes.Operator).RequireAuthenticatedUser());
            options.AddPolicy(
                "RequireOperatorIdentity",
                policy => policy.AddAuthenticationSchemes(JwtSchemes.Operator).RequireClaim(AgoClaimTypes.OperatorId));
            options.AddPolicy("RequirePlatformOwner", policy => policy
                .AddAuthenticationSchemes(JwtSchemes.Operator)
                .RequireAuthenticatedUser()
                .AddRequirements(new PlatformOwnerRequirement()));
        });
        builder.Services.AddSingleton<IAuthorizationHandler, PlatformOwnerAuthorizationHandler>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapSitesEndpoints();
        app.MapOwnerEndpoints();
        app.MapGet("/carried-site-id", (HttpContext context) => Results.Text(context.User.GetSiteId().Value.ToString()))
            .RequireAuthorization("RequireOperatorIdentity");

        await app.StartAsync();
        return app;
    }
}
