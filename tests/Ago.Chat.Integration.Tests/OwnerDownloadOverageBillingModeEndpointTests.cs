using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Owner;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetAttachmentDownloadUrl;
using Ago.Chat.Application.UseCases.SetDownloadOverageBillingModeAsOwner;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-84`'s own platform-owner-only, per-tenant billing-mode toggle, over a real HTTP pipeline, a real
/// Postgres and real Keycloak-signed tokens - the identical shape
/// <see cref="OwnerDownloadBlockExemptionEndpointTests"/> already holds itself to for `25-83`'s own
/// sibling override, and for the identical reason: what matters is not that a handler flipped a field
/// but that the *real download gate* follows, checked by calling
/// <see cref="GetAttachmentDownloadUrlHandler"/> against the same real row the HTTP write just changed.
///
/// <para><b>This file is also where `25-84`'s own Out of scope is proven structurally</b> - "any
/// tenant-facing self-service control over the auto-bill/manual toggle stays the platform owner's." A
/// site-wide `"Admin"` holding every permission their own tenant has is refused here exactly as they
/// are on `25-83`'s route, which is what makes that sentence a property of the system rather than of a
/// console that happens not to render a button.</para>
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class OwnerDownloadOverageBillingModeEndpointTests(OperatorOidcFixture fixture)
{
    private const long OneGibibyte = 1024L * 1024L * 1024L;

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static string OwnerRoute(SiteId siteId) =>
        $"/api/v1/owner/sites/{siteId.Value}/download-overage-billing-mode";

    /// <summary>The item's own headline claim for this half: the owner sets the toggle, and the real
    /// gate follows - both directions. A blocked tenant moved onto auto-bill starts downloading again
    /// with no action of their own whatsoever; moved back to manual, they block again.</summary>
    [Fact]
    public async Task OwnerToken_SetsAutoBillThenManual_AndTheRealDownloadGateFollowsBothTimes()
    {
        var (siteId, visitorId, attachmentId) = await SeedBlockedSiteAsync();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        // Before: manual by default, nothing paid, so the real gate refuses.
        Assert.Equal("Attachment.DownloadBlocked", (await TryDownloadAsync()).Error!.Value.Code);

        var toAutoBill = await ownerClient.PostAsJsonAsync(
            OwnerRoute(siteId),
            new OwnerDownloadOverageBillingModeEndpoints.SetDownloadOverageBillingModeRequest(
                Mode: nameof(DownloadOverageBillingMode.AutoBill), Reason: "tenant prefers a planned invoice line to a surprise block"));
        Assert.Equal(HttpStatusCode.OK, toAutoBill.StatusCode);

        // After: unblocked, with zero tenant action - the whole point of auto-bill.
        Assert.True((await TryDownloadAsync()).IsSuccess);

        var backToManual = await ownerClient.PostAsJsonAsync(
            OwnerRoute(siteId),
            new OwnerDownloadOverageBillingModeEndpoints.SetDownloadOverageBillingModeRequest(
                Mode: nameof(DownloadOverageBillingMode.Manual), Reason: "tenant asked to approve each charge"));
        Assert.Equal(HttpStatusCode.OK, backToManual.StatusCode);

        // Blocked again - a live toggle, not a one-time escape.
        Assert.Equal("Attachment.DownloadBlocked", (await TryDownloadAsync()).Error!.Value.Code);

        async Task<Result<AttachmentDownload>> TryDownloadAsync()
        {
            // `await using` on an `async` local function - the same disposal hazard
            // OwnerDownloadBlockExemptionEndpointTests' own equivalent helper documents in full.
            await using var db = fixture.CreateDbContext();
            return await CreateDownloadHandler(db).HandleAsVisitorAsync(
                new GetAttachmentDownloadUrlAsVisitor(attachmentId, visitorId), CancellationToken.None);
        }
    }

    /// <summary>The audit trail the owner's write leaves on the real row - who, why, when. Read back
    /// off Postgres, never accepted from the response.</summary>
    [Fact]
    public async Task OwnerToken_SetsTheMode_RecordsWhoAndWhy_OnTheRealRow()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            await db.SaveChangesAsync();
        }

        await using var host = await BuildTestHostAsync();
        var token = await fixture.GetPlatformOwnerAccessTokenAsync();
        var ownerClient = CreateClient(host, token);
        var ownerSubject = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(token).Subject;

        const string reason = "Agreed on the onboarding call - they would rather see it on the invoice.";
        var response = await ownerClient.PostAsJsonAsync(
            OwnerRoute(siteId),
            new OwnerDownloadOverageBillingModeEndpoints.SetDownloadOverageBillingModeRequest(
                nameof(DownloadOverageBillingMode.AutoBill), reason));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var verifyDb = fixture.CreateDbContext();
        var site = await new SiteRepository(verifyDb).GetByIdAsync(siteId, CancellationToken.None);
        Assert.Equal(DownloadOverageBillingMode.AutoBill, site!.DownloadOverageBillingMode);
        Assert.Equal(ownerSubject, site.DownloadOverageBillingModeChangedBy);
        Assert.Equal(reason, site.DownloadOverageBillingModeReason);
    }

    [Fact]
    public async Task OwnerToken_WithNoReason_IsRefused_AndChangesNothing()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            await db.SaveChangesAsync();
        }

        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await ownerClient.PostAsJsonAsync(
            OwnerRoute(siteId),
            new OwnerDownloadOverageBillingModeEndpoints.SetDownloadOverageBillingModeRequest(
                nameof(DownloadOverageBillingMode.AutoBill), "   "));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var verifyDb = fixture.CreateDbContext();
        var site = await new SiteRepository(verifyDb).GetByIdAsync(siteId, CancellationToken.None);
        Assert.Equal(DownloadOverageBillingMode.Manual, site!.DownloadOverageBillingMode);
    }

    /// <summary>An unrecognised mode is a `400`, never a silent default - a typo in an owner's request
    /// must not quietly pick a billing arrangement for somebody else's account.</summary>
    [Fact]
    public async Task OwnerToken_WithAnUnknownMode_IsRefused_AndChangesNothing()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            await db.SaveChangesAsync();
        }

        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await ownerClient.PostAsJsonAsync(
            OwnerRoute(siteId),
            new OwnerDownloadOverageBillingModeEndpoints.SetDownloadOverageBillingModeRequest("autobill", "typo"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var verifyDb = fixture.CreateDbContext();
        var site = await new SiteRepository(verifyDb).GetByIdAsync(siteId, CancellationToken.None);
        Assert.Equal(DownloadOverageBillingMode.Manual, site!.DownloadOverageBillingMode);
    }

    // ------------------------------------------------------------------------------------------
    // The authorization boundary - `25-84`'s own Out of scope, enforced by the route rather than by a
    // console that happens not to show a button.
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task OrdinaryOperatorToken_IsRejected()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, await fixture.GetDemoOperatorAccessTokenAsync());

        var response = await client.PostAsJsonAsync(
            OwnerRoute(new SiteId(Guid.NewGuid())),
            new OwnerDownloadOverageBillingModeEndpoints.SetDownloadOverageBillingModeRequest(
                nameof(DownloadOverageBillingMode.AutoBill), "attempted"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>A site-wide `"Admin"` holding every permission their own tenant has is still not the
    /// platform owner - the case `25-84`'s own Out of scope actually turns on.</summary>
    [Fact]
    public async Task SiteConfigureHoldingAdminToken_IsRejected()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, await fixture.GetDemoAdminAccessTokenAsync());

        var response = await client.PostAsJsonAsync(
            OwnerRoute(new SiteId(Guid.NewGuid())),
            new OwnerDownloadOverageBillingModeEndpoints.SetDownloadOverageBillingModeRequest(
                nameof(DownloadOverageBillingMode.AutoBill), "attempted"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NoToken_IsRejected()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token: null);

        var response = await client.PostAsJsonAsync(
            OwnerRoute(new SiteId(Guid.NewGuid())),
            new OwnerDownloadOverageBillingModeEndpoints.SetDownloadOverageBillingModeRequest(
                nameof(DownloadOverageBillingMode.AutoBill), "attempted"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>A site sitting two gibibytes past a one-gibibyte hard threshold, on a tier of its own -
    /// the same real-Postgres setup <see cref="OwnerDownloadBlockExemptionEndpointTests"/> uses, sized
    /// in gibibytes because this item's own charge is priced per gigabyte and a 200-byte fixture would
    /// round to nothing.</summary>
    private async Task<(SiteId SiteId, VisitorId VisitorId, AttachmentId AttachmentId)> SeedBlockedSiteAsync()
    {
        var tier = $"test_{Guid.NewGuid():N}";
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO tier_download_thresholds (tier, soft_threshold_bytes, hard_threshold_bytes, updated_at, updated_by)
                VALUES (@tier, @soft, @hard, now(), 'OwnerDownloadOverageBillingModeEndpointTests')
                """,
                connection);
            command.Parameters.AddWithValue("tier", tier);
            command.Parameters.AddWithValue("soft", OneGibibyte / 2);
            command.Parameters.AddWithValue("hard", OneGibibyte);
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
                attachmentId, siteId, conversationId, $"site/{siteId.Value:N}/{attachmentId.Value:N}.png", "image/png", 50, Now);
            attachment.ConfirmReady(50, "image/png", Now);
            db.Attachments.Add(attachment);
            await db.SaveChangesAsync();
        }

        await new AttachmentEgressMeterStore(fixture.DataSource).RecordAsync(
            siteId, new DateOnly(Now.Year, Now.Month, 1), bytes: 3 * OneGibibyte, CancellationToken.None);

        return (siteId, visitorId, attachmentId);
    }

    private GetAttachmentDownloadUrlHandler CreateDownloadHandler(AgoChatDbContext db) => new(
        new AttachmentRepository(db),
        new ConversationRepository(db),
        new SiteRepository(db),
        new FakeFileStorage(),
        new PermissionChecker(db),
        new NoOpCache(),
        new AttachmentEgressMeterStore(fixture.DataSource),
        new AttachmentEgressReadStore(fixture.DataSource),
        new DownloadThresholdReadStore(fixture.DataSource),
        new DownloadOverageReadStore(fixture.DataSource),
        new PriceCatalogRepository(db),
        new Ago.Chat.Application.UseCases.CreateAttachment.AttachmentOptions(),
        new FixedClock(Now),
        new UuidV7Generator(),
        NullLogger<GetAttachmentDownloadUrlHandler>.Instance);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
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

    /// <summary>The production wiring for this one owner-only route - the same registered-one-by-one
    /// shape <see cref="OwnerDownloadBlockExemptionEndpointTests.BuildTestHostAsync"/> uses and for the
    /// same reason (<c>NpgsqlDataSource.ConnectionString</c> redacts the password).</summary>
    private async Task<WebApplication> BuildTestHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();

        builder.Services.AddSingleton(fixture.DataSource);
        builder.Services.AddDbContext<AgoChatDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>()));
        builder.Services.AddScoped<ISiteRepository, SiteRepository>();
        builder.Services.AddScoped<SetDownloadOverageBillingModeAsOwnerHandler>();
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
            options.AddPolicy("RequirePlatformOwner", policy => policy
                .AddAuthenticationSchemes(JwtSchemes.Operator)
                .RequireAuthenticatedUser()
                .AddRequirements(new PlatformOwnerRequirement())));
        builder.Services.AddSingleton<IAuthorizationHandler, PlatformOwnerAuthorizationHandler>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapOwnerDownloadOverageBillingModeEndpoints();

        await app.StartAsync();
        return app;
    }
}
