using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ago.Chat.Api.Attachments;
using Ago.Chat.Api.Auth;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ConfirmAttachment;
using Ago.Chat.Application.UseCases.CreateAttachment;
using Ago.Chat.Application.UseCases.DeleteAttachment;
using Ago.Chat.Application.UseCases.GetAttachmentDownloadUrl;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-91`'s own Done-when: `GET /api/v1/attachments/{id}` proven over a real HTTP pipeline for
/// success and every error code <see cref="GetAttachmentDownloadUrlHandler"/> can return, so
/// <see cref="Ago.Chat.Api.Http.ErrorExtensions.ToProblem"/>'s mapping for each of them is checked
/// against the wire, not only asserted as a <see cref="Result{T}"/> value the way
/// <c>GetAttachmentDownloadUrlHandlerTests</c> already does at the Application layer. `25-83`'s own
/// two real bugs (`Attachment.DownloadBlocked` and `Site.DownloadBlockExemptionReasonRequired` both
/// silently falling through to `500`) were caught only by accident, through a *different* route's own
/// HTTP test (<see cref="OwnerDownloadBlockExemptionEndpointTests"/>) - this file is what closes the
/// gap that item's own remarks named: nothing exercises this route itself, for any outcome.
///
/// <para><b>Why every case below uses a visitor token, never an operator one.</b>
/// <see cref="GetAttachmentDownloadUrlHandler"/> branches into
/// <c>HandleAsVisitorAsync</c>/<c>HandleAsOperatorAsync</c> only for the participant check (visitor-of-
/// conversation vs. operator-permission-plus-assigned); every error code this item's Done-when names -
/// <c>NotFound</c>, <c>NotReady</c>, <c>Removed</c>, <c>DownloadBlocked</c> - and the success path are
/// all reached from the shared code *after* that check, so the visitor path exercises the identical
/// <c>ToProblem</c> wiring the operator path would. Reaching them through
/// <c>HandleAsOperatorAsync</c> as well would additionally require granting
/// <see cref="Permission.ConversationRead"/> and assigning the seeded operator to each conversation -
/// real setup cost that would not exercise anything this item's own error-mapping claim needs proven a
/// second way. The operator-vs-visitor authorization boundary itself (a permission-holding operator of
/// a *different* site, the same shape <see cref="CrossTenantRouteIsolationTests"/> proves for other
/// route groups) is a distinct claim from this item's own scope and is not asserted here.</para>
///
/// <para><b>Why <see cref="CreateAttachmentHandler"/>/<see cref="ConfirmAttachmentHandler"/>/
/// <see cref="DeleteAttachmentHandler"/> are registered even though no test below calls their routes.</b>
/// <see cref="AttachmentEndpoints.MapAttachmentEndpoints"/> maps all four routes on one group;
/// <c>RouteHandlerDiRegistrationTests</c>' own remarks document why building this host's endpoint
/// metadata (triggered by <c>UseAuthorization()</c> the moment any request is authorized) requires
/// every mapped route handler's constructor parameters to be classifiable by Minimal API's
/// <c>RequestDelegateFactory</c>, not only the one route under test - an unregistered handler type
/// risks exactly `25-06`'s own crash class. <see cref="OwnerDownloadBlockExemptionEndpointTests"/>'s
/// own remarks named this as the real, separate gap standing between that item and mapping this route
/// for real; this file is what pays it off.</para>
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class AttachmentDownloadEndpointTests(OperatorOidcFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string VisitorTokenIssuer = "ago-chat-api";

    private readonly VisitorSigningKeyRing _visitorSigningKeys = TestSigningKeys.Ring();

    [Fact]
    public async Task ReadyAttachment_VisitorParticipant_ReturnsOkWithAPresignedUrl()
    {
        var (visitorId, attachmentId) = await SeedAsync(AttachmentState.Ready);

        await using var host = await BuildTestHostAsync();
        using var client = CreateVisitorClient(host, visitorId);

        var response = await client.GetAsync($"/api/v1/attachments/{attachmentId.Value}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AttachmentEndpoints.AttachmentDownloadResponse>();
        Assert.NotNull(body);
        Assert.Equal("image/png", body!.ContentType);
    }

    [Fact]
    public async Task UnknownAttachmentId_ReturnsNotFound()
    {
        var visitorId = new VisitorId(Guid.NewGuid());

        await using var host = await BuildTestHostAsync();
        using var client = CreateVisitorClient(host, visitorId);

        var response = await client.GetAsync($"/api/v1/attachments/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Attachment.NotFound", await ReadProblemCodeAsync(response));
    }

    [Fact]
    public async Task PendingAttachment_ReturnsConflict_AttachmentNotReady()
    {
        var (visitorId, attachmentId) = await SeedAsync(AttachmentState.Pending);

        await using var host = await BuildTestHostAsync();
        using var client = CreateVisitorClient(host, visitorId);

        var response = await client.GetAsync($"/api/v1/attachments/{attachmentId.Value}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Attachment.NotReady", await ReadProblemCodeAsync(response));
    }

    [Fact]
    public async Task DeletedAttachment_ReturnsGone_AttachmentRemoved()
    {
        var (visitorId, attachmentId) = await SeedAsync(AttachmentState.Deleted);

        await using var host = await BuildTestHostAsync();
        using var client = CreateVisitorClient(host, visitorId);

        var response = await client.GetAsync($"/api/v1/attachments/{attachmentId.Value}");

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Equal("Attachment.Removed", await ReadProblemCodeAsync(response));
    }

    /// <summary>`25-83`'s own defect #1, the reason this whole item exists: before that item's fix,
    /// this exact case answered `500`, not `403`.</summary>
    [Fact]
    public async Task SiteAtHardDownloadThreshold_ReturnsForbidden_AttachmentDownloadBlocked()
    {
        var (visitorId, attachmentId) = await SeedBlockedAsync();

        await using var host = await BuildTestHostAsync();
        using var client = CreateVisitorClient(host, visitorId);

        var response = await client.GetAsync($"/api/v1/attachments/{attachmentId.Value}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Attachment.DownloadBlocked", await ReadProblemCodeAsync(response));
    }

    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        // ErrorExtensions.ToProblem stamps the machine-readable code onto RFC 7807 `title` (and
        // `type`) - api-design.md's own "clients branch on `type`, never on the message." Read via a
        // raw JsonDocument rather than ProblemDetails: the extra `traceId` extension member is not
        // something this test needs to model a DTO for.
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("title").GetString();
    }

    private async Task<(VisitorId VisitorId, AttachmentId AttachmentId)> SeedAsync(AttachmentState state)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var attachmentId = new AttachmentId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        // A fresh, per-test tier name - `tier_download_thresholds` fails open for a tier with no row
        // (DownloadThresholdReadStore's own remarks), so "free" would work too, but a unique tier
        // keeps this test immune to any other test in this shared collection ever seeding a
        // `tier_download_thresholds` row named "free".
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [], tier: $"unbounded_{siteId.Value:N}"));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));

        var attachment = Attachment.CreatePending(
            attachmentId, siteId, conversationId, $"site/{siteId.Value:N}/{attachmentId.Value:N}.png", "image/png", 16, Now);
        if (state is AttachmentState.Ready or AttachmentState.Deleted)
        {
            attachment.ConfirmReady(16, "image/png", Now);
        }

        if (state == AttachmentState.Deleted)
        {
            attachment.MarkDeleted();
        }

        db.Attachments.Add(attachment);
        await db.SaveChangesAsync();

        return (visitorId, attachmentId);
    }

    /// <summary>The identical seeding shape <see cref="OwnerDownloadBlockExemptionEndpointTests"/>'s
    /// own <c>SeedBlockedSiteAsync</c> uses - a real threshold row and a real egress row at-or-above
    /// the hard limit, so <see cref="GetAttachmentDownloadUrlHandler.EnforceDownloadCapAsync"/>
    /// genuinely refuses.</summary>
    private async Task<(VisitorId VisitorId, AttachmentId AttachmentId)> SeedBlockedAsync()
    {
        var tier = $"blocked_{Guid.NewGuid():N}";
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO tier_download_thresholds (tier, soft_threshold_bytes, hard_threshold_bytes, updated_at, updated_by)
                VALUES (@tier, @soft, @hard, now(), 'AttachmentDownloadEndpointTests')
                """,
                connection);
            command.Parameters.AddWithValue("tier", tier);
            command.Parameters.AddWithValue("soft", 100L);
            command.Parameters.AddWithValue("hard", 200L);
            await command.ExecuteNonQueryAsync();
        }

        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var attachmentId = new AttachmentId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [], tier: tier));
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
            var attachment = Attachment.CreatePending(
                attachmentId, siteId, conversationId, $"site/{siteId.Value:N}/{attachmentId.Value:N}.png", "image/png", 16, Now);
            attachment.ConfirmReady(16, "image/png", Now);
            db.Attachments.Add(attachment);
            await db.SaveChangesAsync();
        }

        await new AttachmentEgressMeterStore(fixture.DataSource).RecordAsync(
            siteId, new DateOnly(Now.Year, Now.Month, 1), bytes: 200, CancellationToken.None);

        return (visitorId, attachmentId);
    }

    private HttpClient CreateVisitorClient(WebApplication host, VisitorId visitorId)
    {
        var client = host.GetTestClient();
        var tokens = new JwtTokenService(_visitorSigningKeys, VisitorTokenIssuer, new Ago.Platform.Hosting.SystemClock());
        var token = tokens.IssueVisitorToken(visitorId, fixture.SeededSiteId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>The production route group, mapped for real
    /// (<see cref="AttachmentEndpoints.MapAttachmentEndpoints"/>) against real Postgres - the same
    /// "registered one by one against the fixture's own already-built data source" shape
    /// <see cref="CrossTenantRouteIsolationTests"/> and <see cref="TokenSchemeSeparationTests"/>
    /// already establish for the identical reason (<c>NpgsqlDataSource.ConnectionString</c> redacts
    /// the password, so a connection-string-based registration cannot reuse the fixture's own pool).
    /// A <see cref="Ago.Chat.Integration.Tests.FakeFileStorage"/> stands in for the real S3-backed
    /// <see cref="IFileStorage"/> - proving the presigned URL is genuinely retrievable end to end is
    /// <see cref="AttachmentFixture"/>/<see cref="AttachmentUploadFlowTests"/>'s own job (`5-03`); this
    /// file's job is the HTTP status/route wiring `ErrorExtensions.ToProblem` decides, which never
    /// depends on what the presigned URL actually resolves to.</summary>
    private async Task<WebApplication> BuildTestHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddLogging();

        builder.Services.AddSingleton(fixture.DataSource);
        builder.Services.AddDbContext<AgoChatDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>()));

        // Repositories/services every one of the four AttachmentEndpoints handlers needs, resolved
        // via constructor injection off the registrations below - the identical shape
        // CompositionRoot.ConfigureServices wires in production, restated here only because this host
        // is deliberately narrower than the full app (no Redis, no RabbitMQ, no MinIO - this class's
        // own remarks on why).
        builder.Services.AddScoped<IAttachmentRepository, AttachmentRepository>();
        builder.Services.AddScoped<IConversationRepository, ConversationRepository>();
        builder.Services.AddScoped<ISiteRepository, SiteRepository>();
        builder.Services.AddScoped<IPermissionChecker, PermissionChecker>();
        builder.Services.AddScoped<IConversationAttachmentBudget, ConversationAttachmentBudgetStore>();
        builder.Services.AddScoped<ISiteAttachmentStorageBudget, SiteAttachmentStorageBudgetStore>();
        builder.Services.AddScoped<IBillingSubscriptionRepository, BillingSubscriptionRepository>();
        builder.Services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        builder.Services.AddScoped<IOutboxWriter, EfOutboxWriter<AgoChatDbContext>>();
        builder.Services.AddScoped<IAttachmentEgressMeter, AttachmentEgressMeterStore>();
        builder.Services.AddScoped<IAttachmentEgressReadStore, AttachmentEgressReadStore>();
        builder.Services.AddScoped<IDownloadThresholdReadStore, DownloadThresholdReadStore>();
        builder.Services.AddScoped<IDownloadOverageReadStore, DownloadOverageReadStore>();
        builder.Services.AddScoped<IPriceCatalogRepository, PriceCatalogRepository>();

        builder.Services.AddSingleton<IFileStorage, FakeFileStorage>();
        builder.Services.AddSingleton<IRateLimiter, FakeRateLimiter>();
        builder.Services.AddSingleton<ICache, NoOpCache>();
        // A fixed clock at `Now`, not the real SystemClock - GetAttachmentDownloadUrlHandler.
        // EnforceDownloadCapAsync reads the *current* month off IClock.UtcNow to look up egress, and
        // SeedBlockedAsync records that egress against `Now`'s month (2026-01), not whatever month the
        // test actually runs in. Found live: the first run of
        // SiteAtHardDownloadThreshold_ReturnsForbidden_AttachmentDownloadBlocked answered 200 with
        // SystemClock registered here, because the real "now" and the seeded egress period never
        // matched - the identical FixedClock precedent OwnerDownloadBlockExemptionEndpointTests
        // already uses for the same handler, restated here for the HTTP host rather than a direct call.
        builder.Services.AddSingleton<IClock>(new FixedClock(Now));
        builder.Services.AddSingleton<IIdGenerator, UuidV7Generator>();
        builder.Services.AddSingleton(new AttachmentOptions());
        builder.Services.AddSingleton(new AttachmentRateLimitOptions());
        builder.Services.AddSingleton(new AttachmentStorageQuotaOptions());

        builder.Services.AddScoped<CreateAttachmentHandler>();
        builder.Services.AddScoped<ConfirmAttachmentHandler>();
        builder.Services.AddScoped<DeleteAttachmentHandler>();
        builder.Services.AddScoped<GetAttachmentDownloadUrlHandler>();

        // The operator side of the dual-scheme policy, plus the DELETE route's own
        // "RequireOperatorIdentity" policy - never exercised by a test below, but both need to be
        // real registrations for the identical reason CreateAttachmentHandler/ConfirmAttachmentHandler/
        // DeleteAttachmentHandler do (this class's own remarks): endpoint metadata is built for every
        // mapped route the moment any request is authorized, not only the one a test happens to call.
        // Transcribed from TokenSchemeSeparationTests' own identical block.
        builder.Services.AddScoped<IOperatorRepository, OperatorRepository>();
        builder.Services.AddScoped<ResolveOperatorIdentityHandler>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ISiteActivityWatchdog, SiteActivityWatchdogRepository>();
        builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new SiteActivityWatchdogOptions()));
        builder.Services.AddSingleton<Microsoft.AspNetCore.Authentication.IClaimsTransformation, OperatorIdentityClaimsTransformation>();

        builder.Services.AddAuthentication()
            .AddJwtBearer(JwtSchemes.Visitor, options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = VisitorTokenIssuer,
                    ValidateAudience = true,
                    ValidAudience = JwtSchemes.Visitor,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKeyResolver = (_, _, _, _) => _visitorSigningKeys.ValidationKeys(),
                    ValidateLifetime = true,
                };
            })
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
                };
            });

        builder.Services.AddAuthorization(options => options.AddPolicy(
            "RequireOperatorIdentity",
            policy => policy.AddAuthenticationSchemes(JwtSchemes.Operator).RequireClaim(AgoClaimTypes.OperatorId)));

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapAttachmentEndpoints();

        await app.StartAsync();
        return app;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
