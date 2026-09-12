using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Owner;
using Ago.Chat.Api.Sites;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetSiteExportStatus;
using Ago.Chat.Application.UseCases.GetMessageArchiveDownloadUrl;
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
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `24-16`'s own Done-when, against a real Postgres and real Keycloak-signed tokens
/// (<see cref="OperatorOidcFixture"/>): a platform owner can add and remove a required document with
/// no hand-written SQL, through the real <see cref="OwnerDocumentEndpoints"/> routes this item adds;
/// removing a requirement leaves an already-recorded acceptance untouched (`adr/0111`); and a
/// registration in a deployment where the owner used this exact surface to require one document
/// actually records an acceptance - end to end, the same real-Postgres, real-HTTP-route proof
/// `SiteRegistrationTests.RegisterSite_WhenARequiredTenantDocumentIsPublished_...` already gives for
/// the read side, but here the required-document row is never hand-seeded through a raw
/// <see cref="AgoChatDbContext"/> - it is written by the owner's own new endpoint, which is the one
/// thing that test could not prove and the one thing this item exists to add
/// (`docs/backlog/24-16-*`'s own "proven end to end... since the gap this item closes is exactly the
/// one that handler-level tests could not see").
///
/// <para>Both <see cref="OwnerDocumentEndpoints.MapOwnerDocumentEndpoints"/> (`RequirePlatformOwner`)
/// and <see cref="SitesEndpoints.MapSitesEndpoints"/> (`RequireKeycloakIdentity` on the register route)
/// are mapped on the same host, deliberately - the headline claim can only be proven by doing both,
/// the same reasoning <c>OwnerModuleEndpointsTests</c>'s own remarks give for mapping its own owner and
/// tenant routes together.</para>
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class RequiredDocumentOwnerEndpointsTests(OperatorOidcFixture fixture)
{
    private const string OwnerRoute = "/api/v1/owner/documents/required";

    // ------------------------------------------------------------------------------------------
    // The owner surface itself: list, add, remove - real HTTP, real Postgres.
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task OwnerToken_AddsARequiredDocument_AndItIsListedAfterwards()
    {
        var documentKey = UniqueDocumentKey();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        try
        {
            var addResponse = await ownerClient.PostAsJsonAsync(
                OwnerRoute, new OwnerDocumentEndpoints.AddRequiredDocumentRequest(nameof(AcceptanceSubjectKind.Operator), documentKey));

            Assert.Equal(HttpStatusCode.OK, addResponse.StatusCode);
            var added = await addResponse.Content.ReadFromJsonAsync<OwnerDocumentEndpoints.AddedRequiredDocumentResponse>();
            Assert.NotNull(added);
            Assert.False(added.AlreadyRequired);

            var listResponse = await ownerClient.GetAsync($"{OwnerRoute}/{nameof(AcceptanceSubjectKind.Operator)}");
            Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
            var listed = await listResponse.Content.ReadFromJsonAsync<List<OwnerDocumentEndpoints.RequiredDocumentResponse>>();
            Assert.NotNull(listed);
            Assert.Contains(listed, d => d.DocumentKey == documentKey);
        }
        finally
        {
            await CleanUpAsync(documentKey);
        }
    }

    /// <summary>Idempotent, proven over the real route: a second identical add is a `200`, not an
    /// error, and does not produce a second row - the real unique index behind
    /// <c>RequiredDocumentRepository.AddAsync</c> is never even reached the second time.</summary>
    [Fact]
    public async Task OwnerToken_AddsTheIdenticalRequirementTwice_ReportsAlreadyRequired_AndIsNotAnError()
    {
        var documentKey = UniqueDocumentKey();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        try
        {
            var request = new OwnerDocumentEndpoints.AddRequiredDocumentRequest(nameof(AcceptanceSubjectKind.Operator), documentKey);
            var first = await ownerClient.PostAsJsonAsync(OwnerRoute, request);
            var second = await ownerClient.PostAsJsonAsync(OwnerRoute, request);

            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            var secondBody = await second.Content.ReadFromJsonAsync<OwnerDocumentEndpoints.AddedRequiredDocumentResponse>();
            Assert.NotNull(secondBody);
            Assert.True(secondBody.AlreadyRequired);

            await using var db = fixture.CreateDbContext();
            var rowCount = await db.RequiredDocuments.CountAsync(r => r.DocumentKey == documentKey);
            Assert.Equal(1, rowCount);
        }
        finally
        {
            await CleanUpAsync(documentKey);
        }
    }

    [Fact]
    public async Task OwnerToken_RemovesARequiredDocument_AndItIsNoLongerListed()
    {
        var documentKey = UniqueDocumentKey();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        await ownerClient.PostAsJsonAsync(
            OwnerRoute, new OwnerDocumentEndpoints.AddRequiredDocumentRequest(nameof(AcceptanceSubjectKind.Operator), documentKey));

        var removeResponse = await ownerClient.DeleteAsync($"{OwnerRoute}/{nameof(AcceptanceSubjectKind.Operator)}/{documentKey}");

        Assert.Equal(HttpStatusCode.OK, removeResponse.StatusCode);
        var removed = await removeResponse.Content.ReadFromJsonAsync<OwnerDocumentEndpoints.RemovedRequiredDocumentResponse>();
        Assert.NotNull(removed);
        Assert.True(removed.WasRequired);

        var listResponse = await ownerClient.GetAsync($"{OwnerRoute}/{nameof(AcceptanceSubjectKind.Operator)}");
        var listed = await listResponse.Content.ReadFromJsonAsync<List<OwnerDocumentEndpoints.RequiredDocumentResponse>>();
        Assert.NotNull(listed);
        Assert.DoesNotContain(listed, d => d.DocumentKey == documentKey);
    }

    /// <summary>Idempotent the other direction too - removing an entry that was never there (or
    /// already removed) is a `200` naming <c>WasRequired: false</c>, never a `404` a double-click on an
    /// owner's own console would otherwise have to guard against.</summary>
    [Fact]
    public async Task OwnerToken_RemovesARequirementThatWasNeverThere_ReportsWasRequiredFalse_AndIsNotAnError()
    {
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await ownerClient.DeleteAsync($"{OwnerRoute}/{nameof(AcceptanceSubjectKind.Operator)}/{UniqueDocumentKey()}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OwnerDocumentEndpoints.RemovedRequiredDocumentResponse>();
        Assert.NotNull(body);
        Assert.False(body.WasRequired);
    }

    [Fact]
    public async Task OwnerToken_AddsWithAnUnknownSubjectKind_Returns400_AndAddsNothing()
    {
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await ownerClient.PostAsJsonAsync(
            OwnerRoute, new OwnerDocumentEndpoints.AddRequiredDocumentRequest("NotASubjectKind", UniqueDocumentKey()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OwnerToken_AddsWithAnEmptyDocumentKey_Returns400_AndAddsNothing()
    {
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await ownerClient.PostAsJsonAsync(
            OwnerRoute, new OwnerDocumentEndpoints.AddRequiredDocumentRequest(nameof(AcceptanceSubjectKind.Operator), "   "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ------------------------------------------------------------------------------------------
    // The authorization boundary: RequirePlatformOwner and nothing weaker - the same claim every
    // other owner-only surface in this codebase proves for itself.
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task OrdinaryOperatorToken_CannotAddOrListOrRemoveRequiredDocuments()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, await fixture.GetDemoOperatorAccessTokenAsync());
        var documentKey = UniqueDocumentKey();

        var addResponse = await client.PostAsJsonAsync(
            OwnerRoute, new OwnerDocumentEndpoints.AddRequiredDocumentRequest(nameof(AcceptanceSubjectKind.Operator), documentKey));
        var listResponse = await client.GetAsync($"{OwnerRoute}/{nameof(AcceptanceSubjectKind.Operator)}");
        var removeResponse = await client.DeleteAsync($"{OwnerRoute}/{nameof(AcceptanceSubjectKind.Operator)}/{documentKey}");

        Assert.Equal(HttpStatusCode.Forbidden, addResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, listResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, removeResponse.StatusCode);
    }

    [Fact]
    public async Task NoToken_CannotAddOrListOrRemoveRequiredDocuments()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token: null);
        var documentKey = UniqueDocumentKey();

        var addResponse = await client.PostAsJsonAsync(
            OwnerRoute, new OwnerDocumentEndpoints.AddRequiredDocumentRequest(nameof(AcceptanceSubjectKind.Operator), documentKey));
        var listResponse = await client.GetAsync($"{OwnerRoute}/{nameof(AcceptanceSubjectKind.Operator)}");
        var removeResponse = await client.DeleteAsync($"{OwnerRoute}/{nameof(AcceptanceSubjectKind.Operator)}/{documentKey}");

        Assert.Equal(HttpStatusCode.Unauthorized, addResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, listResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, removeResponse.StatusCode);
    }

    // ------------------------------------------------------------------------------------------
    // `adr/0111`, proven for this item's own write: removing a requirement must not touch an
    // acceptance already recorded against it.
    // ------------------------------------------------------------------------------------------

    /// <summary>The item's own third Done-when, over real Postgres, and the one path where the
    /// requirement itself is genuinely withdrawn <b>after</b> a real acceptance already names it -
    /// registers while the requirement is live (recording the acceptance), then removes the
    /// requirement through the owner's own real route, then re-reads the acceptance row directly. If
    /// `adr/0111` were ever violated by a future change (a foreign key added "for consistency", or a
    /// cascade step assuming the same symmetry other tables use), this is the test that would go
    /// red.</summary>
    [Fact]
    public async Task RemovingARequirement_LeavesAnAlreadyRecordedAcceptanceUntouched()
    {
        var documentKey = UniqueDocumentKey();
        var (token, _) = await fixture.CreateFreshUserAccessTokenAsync();

        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());
        using var registrationClient = CreateClient(host, token);

        try
        {
            // Publish, through the real `24-02` route, then require it for Tenant, through this
            // item's own real route - both writes the platform owner would actually make, not seeded
            // rows.
            await ownerClient.PostAsJsonAsync(
                "/api/v1/owner/documents",
                new OwnerDocumentEndpoints.PublishDocumentRequest(documentKey, "Tenant Terms", "DRAFT v1 - awaiting legal review."));
            await ownerClient.PostAsJsonAsync(
                OwnerRoute, new OwnerDocumentEndpoints.AddRequiredDocumentRequest(nameof(AcceptanceSubjectKind.Tenant), documentKey));

            var registerResponse = await registrationClient.PostAsJsonAsync(
                "/api/v1/sites", new SitesEndpoints.RegisterSiteRequest("Acme Support", "https://shop.example.com"));
            Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);
            var registered = await registerResponse.Content.ReadFromJsonAsync<SitesEndpoints.RegisterSiteResponse>();
            Assert.NotNull(registered);

            await using (var before = fixture.CreateDbContext())
            {
                var acceptance = await before.AcceptanceRecords.SingleAsync(a =>
                    a.SubjectKind == AcceptanceSubjectKind.Tenant && a.SubjectId == registered.SiteId && a.DocumentKey == documentKey);
                Assert.Equal("v1", acceptance.DocumentVersion);
            }

            // The requirement is withdrawn - the acceptance already recorded must survive this
            // untouched, per `adr/0111`.
            var removeResponse = await ownerClient.DeleteAsync(
                $"{OwnerRoute}/{nameof(AcceptanceSubjectKind.Tenant)}/{documentKey}");
            Assert.Equal(HttpStatusCode.OK, removeResponse.StatusCode);

            await using var after = fixture.CreateDbContext();
            var stillThere = await after.AcceptanceRecords.SingleAsync(a =>
                a.SubjectKind == AcceptanceSubjectKind.Tenant && a.SubjectId == registered.SiteId && a.DocumentKey == documentKey);
            Assert.Equal("v1", stillThere.DocumentVersion);
            Assert.Equal(registered.SiteId, stillThere.SubjectId);

            // And the requirement really is gone - a fresh registration under this deployment no
            // longer needs it (the read side `GetRequiredDocumentsForSubjectKindHandler` already
            // proves at the handler level; reconfirmed here through the same real route the owner
            // used to remove it).
            var listResponse = await ownerClient.GetAsync($"{OwnerRoute}/{nameof(AcceptanceSubjectKind.Tenant)}");
            var listed = await listResponse.Content.ReadFromJsonAsync<List<OwnerDocumentEndpoints.RequiredDocumentResponse>>();
            Assert.NotNull(listed);
            Assert.DoesNotContain(listed, d => d.DocumentKey == documentKey);
        }
        finally
        {
            await CleanUpAsync(documentKey);
        }
    }

    // ------------------------------------------------------------------------------------------
    // The item's own headline Done-when, end to end: the platform owner uses the real surface this
    // item adds, and a registration in that deployment actually records an acceptance because of it.
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Unlike <c>SiteRegistrationTests.RegisterSite_WhenARequiredTenantDocumentIsPublished_...</c>,
    /// which seeds `required_documents` by writing straight onto a raw <see cref="AgoChatDbContext"/>
    /// because (its own remarks say) "no admin endpoint exists yet to do it through", this test seeds
    /// nothing directly - the platform owner publishes and requires the document through the two real
    /// HTTP routes this item and `24-02` add, and only then does a fresh identity register. This is the
    /// proof the backlog item's own Done-when demands and the handler-level tests above cannot give:
    /// before this item existed, there was no route here to call at all.
    /// </summary>
    [Fact]
    public async Task OwnerPublishesAndRequiresADocumentThroughTheRealRoutes_AndARegistrationInThatDeploymentRecordsAnAcceptance()
    {
        var documentKey = UniqueDocumentKey();
        var (token, _) = await fixture.CreateFreshUserAccessTokenAsync();

        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());
        using var registrationClient = CreateClient(host, token);

        try
        {
            var publishResponse = await ownerClient.PostAsJsonAsync(
                "/api/v1/owner/documents",
                new OwnerDocumentEndpoints.PublishDocumentRequest(documentKey, "Tenant Terms", "DRAFT v1 - awaiting legal review."));
            Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);

            var addResponse = await ownerClient.PostAsJsonAsync(
                OwnerRoute, new OwnerDocumentEndpoints.AddRequiredDocumentRequest(nameof(AcceptanceSubjectKind.Tenant), documentKey));
            Assert.Equal(HttpStatusCode.OK, addResponse.StatusCode);

            var registerResponse = await registrationClient.PostAsJsonAsync(
                "/api/v1/sites", new SitesEndpoints.RegisterSiteRequest("Acme Support", "https://shop.example.com"));

            Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);
            var registered = await registerResponse.Content.ReadFromJsonAsync<SitesEndpoints.RegisterSiteResponse>();
            Assert.NotNull(registered);

            await using var db = fixture.CreateDbContext();
            var acceptance = await db.AcceptanceRecords.SingleAsync(a =>
                a.SubjectKind == AcceptanceSubjectKind.Tenant && a.SubjectId == registered.SiteId && a.DocumentKey == documentKey);
            Assert.Equal("v1", acceptance.DocumentVersion);

            var readBack = await new DocumentRepository(db).FindVersionAsync(documentKey, acceptance.DocumentVersion, CancellationToken.None);
            Assert.NotNull(readBack);
            Assert.Equal("Tenant Terms", readBack!.Title);
            Assert.Equal("DRAFT v1 - awaiting legal review.", readBack.Body);
        }
        finally
        {
            await CleanUpAsync(documentKey);
        }
    }

    /// <summary>The complement: once the owner removes the requirement through the real route, a
    /// *fresh* identity's registration no longer needs to record anything for this key - the read
    /// path (`RegisterSiteHandler`) reacting to this item's own write in the other direction.</summary>
    [Fact]
    public async Task OwnerRemovesARequiredDocumentThroughTheRealRoute_AndALaterRegistrationRecordsNoAcceptanceForIt()
    {
        var documentKey = UniqueDocumentKey();
        var (token, _) = await fixture.CreateFreshUserAccessTokenAsync();

        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());
        using var registrationClient = CreateClient(host, token);

        try
        {
            await ownerClient.PostAsJsonAsync(
                "/api/v1/owner/documents",
                new OwnerDocumentEndpoints.PublishDocumentRequest(documentKey, "Tenant Terms", "DRAFT v1."));
            await ownerClient.PostAsJsonAsync(
                OwnerRoute, new OwnerDocumentEndpoints.AddRequiredDocumentRequest(nameof(AcceptanceSubjectKind.Tenant), documentKey));

            var removeResponse = await ownerClient.DeleteAsync($"{OwnerRoute}/{nameof(AcceptanceSubjectKind.Tenant)}/{documentKey}");
            Assert.Equal(HttpStatusCode.OK, removeResponse.StatusCode);

            var registerResponse = await registrationClient.PostAsJsonAsync(
                "/api/v1/sites", new SitesEndpoints.RegisterSiteRequest("Acme Support", "https://shop.example.com"));
            Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);
            var registered = await registerResponse.Content.ReadFromJsonAsync<SitesEndpoints.RegisterSiteResponse>();
            Assert.NotNull(registered);

            await using var db = fixture.CreateDbContext();
            var recordedForThisKey = await db.AcceptanceRecords.AnyAsync(a =>
                a.SubjectKind == AcceptanceSubjectKind.Tenant && a.SubjectId == registered.SiteId && a.DocumentKey == documentKey);
            Assert.False(recordedForThisKey);
        }
        finally
        {
            await CleanUpAsync(documentKey);
        }
    }

    // ------------------------------------------------------------------------------------------

    private static string UniqueDocumentKey() => $"required-doc-owner-{Guid.NewGuid():N}";

    /// <summary><see cref="OperatorOidcFixture"/> is shared, real Postgres state across this entire
    /// collection (<see cref="SiteRegistrationTests"/> included) - a `required_documents`/`documents`
    /// row this file writes through the real owner routes is exactly as real and exactly as global as
    /// one seeded by hand, so it must be cleaned up here for the identical reason
    /// <c>SiteRegistrationTests</c>'s own sibling tests already clean up after themselves.</summary>
    private async Task CleanUpAsync(string documentKey)
    {
        await using var db = fixture.CreateDbContext();
        await db.RequiredDocuments.Where(r => r.DocumentKey == documentKey).ExecuteDeleteAsync();
        await db.PublishedDocumentVersions.Where(v => v.DocumentKey == documentKey).ExecuteDeleteAsync();
        await db.Documents.Where(d => d.DocumentKey == documentKey).ExecuteDeleteAsync();
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

    /// <summary>A real <see cref="WebApplication"/> mapping the real production
    /// <see cref="OwnerDocumentEndpoints.MapOwnerDocumentEndpoints"/> and
    /// <see cref="SitesEndpoints.MapSitesEndpoints"/> together - the union of
    /// <c>SiteRegistrationTests.BuildTestHostAsync</c>'s own registrations (everything
    /// <c>MapSitesEndpoints</c> needs, including the export/archive routes it maps internally, per that
    /// file's own remarks on why an unused route's handler still has to resolve) and this file's own
    /// three handlers behind <see cref="OwnerDocumentEndpoints"/>'s required-document routes. Skips
    /// `MapTenantAgreementsEndpoint` and its own `IAcceptanceRepository`/`GetTenantAgreementsForSiteHandler`
    /// registrations - this file never calls that route, and it is `SitesEndpoints`'s own separate
    /// `Map` call, not part of `MapSitesEndpoints` itself.</summary>
    private async Task<WebApplication> BuildTestHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddRouting();
        builder.Services.AddSingleton(fixture.DataSource);
        builder.Services.AddDbContext<AgoChatDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<Npgsql.NpgsqlDataSource>()));

        // `SitesEndpoints`'s own registration route.
        builder.Services.AddScoped<IOperatorRepository, OperatorRepository>();
        builder.Services.AddScoped<ISiteRegistrationRepository, SiteRegistrationRepository>();
        builder.Services.AddScoped<IOutboxWriter, EfOutboxWriter<AgoChatDbContext>>();
        builder.Services.AddScoped<IRequiredDocumentRepository, RequiredDocumentRepository>();
        builder.Services.AddScoped<IDocumentRepository, DocumentRepository>();
        builder.Services.AddScoped<ResolveOperatorIdentityHandler>();
        builder.Services.AddScoped<RegisterSiteHandler>();

        // `SitesEndpoints`'s own export/message-archive routes - mapped internally by
        // `MapSitesEndpoints`, so every handler behind them must resolve even though this file never
        // calls them (`SiteRegistrationTests.BuildTestHostAsync`'s own remarks on why).
        builder.Services.AddScoped<IExportRequestRepository, ExportRequestRepository>();
        builder.Services.AddScoped<IPermissionChecker, PermissionChecker>();
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
        // `23-73`: OperatorIdentityClaimsTransformation's own new dependencies - the watchdog
        // reset hook and (where this host did not already have one) IClock.
        builder.Services.AddScoped<Ago.Chat.Application.Abstractions.ISiteActivityWatchdog, Ago.Chat.Infrastructure.Postgres.SiteActivityWatchdogRepository>();
        builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new Ago.Chat.Infrastructure.Postgres.SiteActivityWatchdogOptions()));
        builder.Services.AddSingleton<IClaimsTransformation, OperatorIdentityClaimsTransformation>();
        builder.Services.AddSingleton<IRateLimiter, FakeRateLimiter>();
        builder.Services.AddSingleton(new RegisterSiteRateLimitOptions());
        builder.Services.AddSingleton<IIdGenerator, UuidV7Generator>();
        builder.Services.AddSingleton<IClock, Ago.Platform.Hosting.SystemClock>();

        // `24-16`/`24-02`: `OwnerDocumentEndpoints`'s own three-plus-one routes - publish (already
        // `24-02`'s), list/add/remove (this item's own). `ICache` is a real requirement of
        // `PublishDocumentVersionHandler`'s own constructor, not this file's choice - `NoOpCache` is the
        // identical no-behaviour double `PublishedDocumentIntegrationTests` already uses for the same
        // reason (this file is about the wire and the database, not about cache-hit behaviour).
        builder.Services.AddScoped<Ago.Chat.Application.UseCases.PublishDocumentVersion.PublishDocumentVersionHandler>();
        builder.Services.AddScoped<Ago.Chat.Application.UseCases.GetRequiredDocumentsForSubjectKind.GetRequiredDocumentsForSubjectKindHandler>();
        builder.Services.AddScoped<Ago.Chat.Application.UseCases.AddRequiredDocument.AddRequiredDocumentHandler>();
        builder.Services.AddScoped<Ago.Chat.Application.UseCases.RemoveRequiredDocument.RemoveRequiredDocumentHandler>();
        builder.Services.AddSingleton<ICache, NoOpCache>();

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
        app.MapOwnerDocumentEndpoints();

        await app.StartAsync();
        return app;
    }
}
