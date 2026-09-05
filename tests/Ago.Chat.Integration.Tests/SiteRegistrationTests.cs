using System.IdentityModel.Tokens.Jwt;
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
/// `10-02`'s own Done-when, against a real Keycloak and a real Postgres
/// (<see cref="OperatorOidcFixture"/>): a real registration produces real, queryable rows; the
/// created operator's token subsequently works through `RequireOperatorIdentity`; a second call from
/// the same identity is rejected `409` with a real second call, not asserted from the handler's logic
/// alone. Every test mints its own fresh Keycloak user
/// (<see cref="OperatorOidcFixture.CreateFreshUserAccessTokenAsync"/>) rather than sharing one, since
/// registration is inherently a one-time state transition for a given identity.
///
/// Uses the real `Ago.Chat.Api.Sites.SitesEndpoints`/`RegisterSiteHandler`/
/// `SiteRegistrationRepository` production code against a minimal <see cref="TestServer"/> host, the
/// same seam <see cref="OperatorOidcAuthenticationTests"/> already established - `IRateLimiter` is the
/// in-repo always-allow <see cref="FakeRateLimiter"/> here deliberately: this file is about the
/// registration transaction and the identity round-trip, not rate limiting, which
/// `Ago.Chat.Concurrency.Tests.RegisterSiteRateLimitingConcurrencyTests` covers with a real Redis
/// bucket under real concurrency instead.
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class SiteRegistrationTests(OperatorOidcFixture fixture)
{
    [Fact]
    public async Task RegisterSite_WithARealKeycloakToken_CreatesOneSiteBothRolesOneOperatorAndBothOperatorRoles()
    {
        var (token, _) = await fixture.CreateFreshUserAccessTokenAsync();
        var externalSubjectId = ReadSubjectClaim(token);

        await using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync(
            "/api/v1/sites", new SitesEndpoints.RegisterSiteRequest("Acme Support", "https://shop.example.com"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SitesEndpoints.RegisterSiteResponse>();
        Assert.NotNull(body);
        Assert.Equal($"/api/v1/sites/{body.SiteId}", response.Headers.Location?.OriginalString);

        // Queried directly, not just asserted from the 201 - `10-02`'s own Done-when.
        await using var db = fixture.CreateDbContext();
        var site = await db.Sites.SingleAsync(s => s.Id == new SiteId(body.SiteId));
        Assert.Equal("Acme Support", site.Name);
        Assert.Contains("https://shop.example.com", site.AllowedOrigins);

        var operatorRow = await db.Operators.SingleAsync(o => o.Id == new OperatorId(body.OperatorId));
        Assert.Equal(site.Id, operatorRow.SiteId);
        Assert.Equal(externalSubjectId, operatorRow.ExternalSubjectId);

        var roles = await db.Roles.Where(r => r.SiteId == site.Id).ToListAsync();
        Assert.Equal(2, roles.Count);
        var operatorRole = Assert.Single(roles, r => r.Name == "Operator");
        Assert.Equal(
            [
                Permission.ConversationRead.Value, Permission.ConversationSend.Value, Permission.ConversationAssign.Value,
                Permission.ConversationNoteWrite.Value, Permission.ConversationTag.Value,
                // `22-05`/`adr/0093`: the calendar's own day-to-day permissions, joined here unchanged.
                Permission.BookingConfirm.Value, Permission.BookingReject.Value, Permission.BookingCancel.Value,
                Permission.BookingMarkNoShow.Value, Permission.CustomerRead.Value, Permission.CustomerEdit.Value,
            ],
            operatorRole.Permissions);
        var adminRole = Assert.Single(roles, r => r.Name == "Admin");
        Assert.Equal(
            [
                Permission.SiteConfigure.Value, Permission.SiteManageOperators.Value, Permission.AttachmentDelete.Value,
                Permission.SiteErase.Value, Permission.ConversationErase.Value, Permission.SiteExport.Value,
                Permission.ConversationExport.Value,
                // `22-05`/`adr/0093`: the calendar's own configuration permission, joined here unchanged.
                Permission.CalendarConfigure.Value,
                // `24-12`: the tenant's own read of who accessed their data.
            ],
            adminRole.Permissions);

        var operatorRoleIds = await db.OperatorRoles
            .Where(or => or.OperatorId == operatorRow.Id)
            .Select(or => or.RoleId)
            .ToListAsync();
        Assert.Equal(2, operatorRoleIds.Count);
        Assert.Contains(operatorRole.Id, operatorRoleIds);
        Assert.Contains(adminRole.Id, operatorRoleIds);
    }

    [Fact]
    public async Task RegisterSite_TheCreatedOperatorsToken_SubsequentlyWorksThroughRequireOperatorIdentity()
    {
        var (registrationToken, username) = await fixture.CreateFreshUserAccessTokenAsync();

        await using var host = await BuildTestHostAsync();
        using (var client = host.GetTestClient())
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registrationToken);
            var response = await client.PostAsJsonAsync(
                "/api/v1/sites", new SitesEndpoints.RegisterSiteRequest("Acme Support", "https://shop.example.com"));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        // A *second* token for the same identity - OperatorIdentityClaimsTransformation resolves
        // OperatorId/SiteId at request time from whatever `operators` row now matches `sub`, so a
        // fresh token (not the one used to register) proves the resolution is real, not an artifact
        // of some claim the registration call itself happened to carry.
        var operatorToken = await fixture.RefreshAccessTokenAsync(username);

        using var operatorClient = host.GetTestClient();
        operatorClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", operatorToken);
        var operatorOnlyResponse = await operatorClient.GetAsync("/operator-only");

        Assert.Equal(HttpStatusCode.OK, operatorOnlyResponse.StatusCode);
    }

    /// <summary>
    /// `13-07`/`adr/0068`'s own Done-when, replacing what used to be
    /// "...Returns409_NotASecondSite": before this item, a second `POST /api/v1/sites` from the same
    /// identity was refused `409` (the "one login, one tenant" constraint `10-02` deliberately shipped
    /// and this item deliberately relaxes). Now it succeeds, and produces a real, independently
    /// queryable second `Site`/`Operator` pair - `external_subject_id` equal, `site_id` different,
    /// verified by querying the rows directly rather than trusting a second `201` alone, exactly as
    /// this item's own Done-when demands. Fails before this item's change (the old code 409'd on the
    /// second call) and passes after.
    /// </summary>
    [Fact]
    public async Task RegisterSite_ASecondCallFromTheSameIdentity_CreatesASecondSiteAndOperatorRow()
    {
        var (token, _) = await fixture.CreateFreshUserAccessTokenAsync();

        await using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var first = await client.PostAsJsonAsync(
            "/api/v1/sites", new SitesEndpoints.RegisterSiteRequest("Acme Support", "https://shop.example.com"));
        var second = await client.PostAsJsonAsync(
            "/api/v1/sites", new SitesEndpoints.RegisterSiteRequest("Acme Support - Second Shop", "https://other.example.com"));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var firstBody = await first.Content.ReadFromJsonAsync<SitesEndpoints.RegisterSiteResponse>();
        var secondBody = await second.Content.ReadFromJsonAsync<SitesEndpoints.RegisterSiteResponse>();
        Assert.NotNull(firstBody);
        Assert.NotNull(secondBody);
        Assert.NotEqual(firstBody.SiteId, secondBody.SiteId);
        Assert.NotEqual(firstBody.OperatorId, secondBody.OperatorId);

        var externalSubjectId = ReadSubjectClaim(token);
        await using var db = fixture.CreateDbContext();
        var operatorRows = await db.Operators.Where(o => o.ExternalSubjectId == externalSubjectId).ToListAsync();
        Assert.Equal(2, operatorRows.Count);
        Assert.Contains(operatorRows, o => o.SiteId == new SiteId(firstBody.SiteId));
        Assert.Contains(operatorRows, o => o.SiteId == new SiteId(secondBody.SiteId));

        var siteRows = await db.Sites
            .Where(s => s.Id == new SiteId(firstBody.SiteId) || s.Id == new SiteId(secondBody.SiteId))
            .ToListAsync();
        Assert.Equal(2, siteRows.Count);
    }

    /// <summary>
    /// `24-03`'s own "What must be demonstrated": registration records an acceptance naming a real
    /// published version, and that recorded version resolves to readable text - against a real
    /// Postgres and a real Keycloak-issued token, not fakes. Seeds `required_documents` and a
    /// published `Document` directly (no admin endpoint exists yet to do it through - this item's own
    /// port is read-only, `IRequiredDocumentRepository`'s own remarks), then proves the round trip:
    /// the site is created, exactly one <see cref="AcceptanceRecord"/> exists for it naming the
    /// published version, and reading that exact version back through <see cref="DocumentRepository"/>
    /// (the same production port <c>GetDocumentVersionHandler</c> uses) returns the text that was
    /// actually published, not merely a matching identifier.
    /// </summary>
    [Fact]
    public async Task RegisterSite_WhenARequiredTenantDocumentIsPublished_RecordsAnAcceptance_ThatResolvesToTheReadableText()
    {
        var (token, _) = await fixture.CreateFreshUserAccessTokenAsync();
        var documentKey = $"tenant-terms-{Guid.NewGuid():N}";

        await using (var seedDb = fixture.CreateDbContext())
        {
            seedDb.RequiredDocuments.Add(new RequiredDocumentRecord
            {
                Id = Guid.NewGuid(),
                SubjectKind = AcceptanceSubjectKind.Tenant,
                DocumentKey = documentKey,
            });
            var document = Document.Create(new DocumentId(Guid.NewGuid()), documentKey);
            document.Publish(
                new PublishedDocumentVersionId(Guid.NewGuid()), "Tenant Terms", "DRAFT v1 - awaiting legal review.", DateTimeOffset.UtcNow);
            seedDb.Documents.Add(document);
            await seedDb.SaveChangesAsync();
        }

        try
        {
            await using var host = await BuildTestHostAsync();
            using var client = host.GetTestClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await client.PostAsJsonAsync(
                "/api/v1/sites", new SitesEndpoints.RegisterSiteRequest("Acme Support", "https://shop.example.com"));

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<SitesEndpoints.RegisterSiteResponse>();
            Assert.NotNull(body);

            await using var db = fixture.CreateDbContext();
            var acceptance = await db.AcceptanceRecords.SingleAsync(a =>
                a.SubjectKind == AcceptanceSubjectKind.Tenant && a.SubjectId == body.SiteId && a.DocumentKey == documentKey);
            Assert.Equal("v1", acceptance.DocumentVersion);

            var repository = new DocumentRepository(db);
            var readBack = await repository.FindVersionAsync(documentKey, acceptance.DocumentVersion, CancellationToken.None);
            Assert.NotNull(readBack);
            Assert.Equal("Tenant Terms", readBack!.Title);
            Assert.Equal("DRAFT v1 - awaiting legal review.", readBack.Body);
        }
        finally
        {
            // `OperatorOidcFixture` is shared across every test in this collection
            // (`ActiveSiteResolutionTests` included) - a `required_documents` row this test seeds is
            // real, global state with no delete method on its own production port
            // (`IRequiredDocumentRepository`'s own remarks), so this test must remove it itself or
            // every other registration in the collection would also be asked to accept it for the
            // rest of the run. Found live: the sibling test below left exactly this kind of row
            // behind before this cleanup existed, and `ActiveSiteResolutionTests` started failing
            // registration with `Site.AgreementUnavailable` instead of `Created` as a result.
            await using var cleanup = fixture.CreateDbContext();
            await cleanup.RequiredDocuments.Where(r => r.DocumentKey == documentKey).ExecuteDeleteAsync();
            await cleanup.PublishedDocumentVersions.Where(v => v.DocumentKey == documentKey).ExecuteDeleteAsync();
            await cleanup.Documents.Where(d => d.DocumentKey == documentKey).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// `24-03`'s own registration-blocking case: the owner declared <paramref name="documentKey"/>
    /// (a fresh, unpublished one, seeded below) required before publishing anything under it. Real
    /// host, real database - registration must fail with `Site.AgreementUnavailable` and create
    /// neither a `Site` nor an `Operator` row for this identity, proven by querying directly rather
    /// than trusting the response status alone.
    /// </summary>
    [Fact]
    public async Task RegisterSite_WhenARequiredTenantDocumentHasNoPublishedVersion_Returns503_AndCreatesNoSiteOrOperator()
    {
        var (token, _) = await fixture.CreateFreshUserAccessTokenAsync();
        var externalSubjectId = ReadSubjectClaim(token);
        var documentKey = $"tenant-terms-{Guid.NewGuid():N}";

        await using (var seedDb = fixture.CreateDbContext())
        {
            seedDb.RequiredDocuments.Add(new RequiredDocumentRecord
            {
                Id = Guid.NewGuid(),
                SubjectKind = AcceptanceSubjectKind.Tenant,
                DocumentKey = documentKey,
            });
            // Deliberately no Document/PublishedDocumentVersion under this key.
            await seedDb.SaveChangesAsync();
        }

        try
        {
            await using var host = await BuildTestHostAsync();
            using var client = host.GetTestClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await client.PostAsJsonAsync(
                "/api/v1/sites", new SitesEndpoints.RegisterSiteRequest("Acme Support", "https://shop.example.com"));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            Assert.Equal("Site.AgreementUnavailable", problem.GetProperty("type").GetString());

            await using var db = fixture.CreateDbContext();
            Assert.False(await db.Operators.AnyAsync(o => o.ExternalSubjectId == externalSubjectId));
        }
        finally
        {
            // See the sibling test above's own remarks - this row is real, global, undeleted-by-design
            // state on a fixture shared with every other test in this collection, and this is the test
            // whose leaked row actually broke `ActiveSiteResolutionTests` before this cleanup existed.
            await using var cleanup = fixture.CreateDbContext();
            await cleanup.RequiredDocuments.Where(r => r.DocumentKey == documentKey).ExecuteDeleteAsync();
        }
    }

    // `12-04` added a test here asserting this endpoint answered `403` to a platform-owner token, and
    // `12-05` removed both the refusal and the test (`adr/0063`, "Reversed in 12-05"). The identity
    // that holds both the `platform-owner` realm role and an `operators` row is not a variation on
    // this file's subject - it is its own claim, about two endpoints at once - so it lives in
    // `PlatformOwnerAsTenantTests` rather than as a fifth case here. This file keeps what it always
    // was about: what `POST /api/v1/sites` does for an ordinary self-registering identity.

    private static string ReadSubjectClaim(string jwt) =>
        new JwtSecurityTokenHandler().ReadJwtToken(jwt).Subject;

    /// <summary>A real <see cref="WebApplication"/>, not the generic <c>HostBuilder</c> seam
    /// <see cref="OperatorOidcAuthenticationTests"/>/<see cref="KeycloakIdentityPolicyTests"/> use -
    /// <c>SitesEndpoints.MapSitesEndpoints</c> (like every other endpoints file in this codebase) is
    /// typed against <see cref="WebApplication"/>, so building one here is what lets this test call
    /// the exact production mapping instead of hand-rolling a second copy of its route/handler.</summary>
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
        // `24-03`: RegisterSiteHandler's own two new dependencies - registered here even though most
        // tests in this file leave `required_documents` empty (so they resolve to zero required keys,
        // unchanged behaviour), the same "every handler this host maps must resolve from its own
        // container" reasoning the export/message-archive registrations below already state.
        builder.Services.AddScoped<IRequiredDocumentRepository, RequiredDocumentRepository>();
        builder.Services.AddScoped<IDocumentRepository, DocumentRepository>();
        builder.Services.AddScoped<ResolveOperatorIdentityHandler>();
        builder.Services.AddScoped<RegisterSiteHandler>();
        // `16-03`: SitesEndpoints now also maps the export routes - every handler for every route it
        // maps must resolve from this host's own container, even one this test never calls, because
        // ASP.NET Core builds every mapped endpoint's metadata eagerly the first time any request is
        // authorized (FakeFileStorage's own remarks explain why a fake, not a real S3FileStorage, is
        // enough here).
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
        // `13-07`: OperatorIdentityClaimsTransformation now reads the active-site signal off the
        // current request - see Program.cs's own remarks on this exact registration.
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

        // The real production mapping - no duplicated route/handler logic.
        app.MapSitesEndpoints();
        app.MapGet("/operator-only", (HttpContext _) => Results.Ok())
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = JwtSchemes.Operator,
                Policy = "RequireOperatorIdentity",
            });

        await app.StartAsync();
        return app;
    }
}
