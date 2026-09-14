using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Owner;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetAttachmentDownloadUrl;
using Ago.Chat.Application.UseCases.SetDownloadBlockExemptionAsOwner;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Microsoft.AspNetCore.Authentication;
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
/// `25-83`'s own owner-only override, over a real HTTP pipeline, a real Postgres and real
/// Keycloak-signed tokens (<see cref="OperatorOidcFixture"/>) - the identical "proven end to end, not
/// only at the handler" posture <see cref="OwnerModuleEndpointsTests"/> already holds itself to for a
/// different owner-only write. `OwnerDownloadBlockExemptionEndpointTests_GrantsAndRevokes...` below is
/// this item's own explicit demand: "proven to actually bypass the hard block once granted" - checked
/// by calling the real <see cref="GetAttachmentDownloadUrlHandler"/> gate (against the same real
/// Postgres row the HTTP write just changed) before and after each toggle, not by re-reading the flag
/// the write itself just set.
///
/// <para><b>Only <see cref="OwnerDownloadBlockExemptionEndpoints"/> is mapped on this host</b> - the
/// download gate itself is exercised by constructing <see cref="GetAttachmentDownloadUrlHandler"/>
/// directly against `fixture`'s real Postgres, the identical shape
/// <see cref="SiteAttachmentStorageHandlersTests"/> already uses for every one of its own
/// real-Postgres proofs of that handler, rather than by also mapping the real
/// `GET /api/v1/attachments/{attachmentId}` route (`Ago.Chat.Api.Attachments.AttachmentEndpoints`'s
/// own `HandleDownloadAsync` - it does exist, and does call this exact handler; an earlier draft of
/// this remark said otherwise and was wrong). This file's own job is proving the *owner's write* is
/// real end to end and that its *effect* on the gate is real, not re-proving that route's own HTTP
/// wiring - which nothing in this codebase does yet, for any of its failure codes, not only this
/// item's own <c>Attachment.DownloadBlocked</c>; standing up that route's own host needs
/// `CreateAttachmentHandler`/`ConfirmAttachmentHandler`/`DeleteAttachmentHandler` registered
/// alongside it too (`AttachmentEndpoints.MapAttachmentEndpoints` maps all four on one group), which
/// is a real, separate gap this item's own report flags rather than silently papers over by building
/// it here under a different item's name.</para>
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class OwnerDownloadBlockExemptionEndpointTests(OperatorOidcFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static string OwnerRoute(SiteId siteId) => $"/api/v1/owner/sites/{siteId.Value}/download-block-exemption";

    /// <summary>
    /// The item's own headline claim, both directions, over the real route and a real download gate:
    /// a site sitting at its hard threshold is genuinely blocked, the owner's real `POST` genuinely
    /// lifts it, and a second real `POST` genuinely re-imposes it - each transition checked against
    /// <see cref="GetAttachmentDownloadUrlHandler"/>, not against the write's own `200`.
    /// </summary>
    [Fact]
    public async Task OwnerToken_GrantsThenRevokesTheExemption_AndTheRealDownloadGateFollowsBothTimes()
    {
        var (siteId, conversationId, visitorId, attachmentId) = await SeedBlockedSiteAsync();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        // Before: the real gate refuses.
        Assert.Equal("Attachment.DownloadBlocked", (await TryDownloadAsync(visitorId, attachmentId)).Error!.Value.Code);

        var grantResponse = await ownerClient.PostAsJsonAsync(
            OwnerRoute(siteId), new OwnerDownloadBlockExemptionEndpoints.SetDownloadBlockExemptionRequest(
                Exempt: true, Reason: "goodwill exception while the tenant sorts out their usage"));
        Assert.Equal(HttpStatusCode.OK, grantResponse.StatusCode);

        // After the grant: the real gate now lets it through.
        Assert.True((await TryDownloadAsync(visitorId, attachmentId)).IsSuccess);

        var revokeResponse = await ownerClient.PostAsJsonAsync(
            OwnerRoute(siteId), new OwnerDownloadBlockExemptionEndpoints.SetDownloadBlockExemptionRequest(
                Exempt: false, Reason: "exception period ended"));
        Assert.Equal(HttpStatusCode.OK, revokeResponse.StatusCode);

        // After the revoke: blocked again - the override is a live toggle, not a one-time escape.
        Assert.Equal("Attachment.DownloadBlocked", (await TryDownloadAsync(visitorId, attachmentId)).Error!.Value.Code);

        async Task<Result<AttachmentDownload>> TryDownloadAsync(VisitorId forVisitor, AttachmentId forAttachment)
        {
            // `await using`, and awaited to completion before the context is disposed - a bare
            // `using` on a non-`async` local function disposes the context the moment the (still
            // unfinished) `Task` is returned, not once that task actually completes, which is exactly
            // the `ObjectDisposedException` this test's own fails-before run hit.
            await using var db = fixture.CreateDbContext();
            var handler = CreateDownloadHandler(db);
            return await handler.HandleAsVisitorAsync(new GetAttachmentDownloadUrlAsVisitor(forAttachment, forVisitor), CancellationToken.None);
        }
    }

    /// <summary>The audit trail the owner's own write leaves on the aggregate itself -
    /// <see cref="Site.DownloadBlockExemptionChangedBy"/>/<see cref="Site.DownloadBlockExemptionReason"/>,
    /// read back off the real row, not merely accepted from the response.</summary>
    [Fact]
    public async Task OwnerToken_GrantsTheExemption_RecordsWhoAndWhy_OnTheRealRow()
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

        const string reason = "Tenant is a known pilot customer, exempted while their real ceiling is negotiated.";
        var response = await ownerClient.PostAsJsonAsync(
            OwnerRoute(siteId), new OwnerDownloadBlockExemptionEndpoints.SetDownloadBlockExemptionRequest(Exempt: true, Reason: reason));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var verifyDb = fixture.CreateDbContext();
        var site = await new SiteRepository(verifyDb).GetByIdAsync(siteId, CancellationToken.None);
        Assert.True(site!.DownloadBlockExempt);
        Assert.Equal(ownerSubject, site.DownloadBlockExemptionChangedBy);
        Assert.Equal(reason, site.DownloadBlockExemptionReason);
    }

    /// <summary>`SetDownloadBlockExemptionAsOwnerHandler`'s own guard, reached this time through the
    /// real HTTP body - a blank reason is refused before the flag is ever touched.</summary>
    [Fact]
    public async Task OwnerToken_WithNoReason_IsRefused_AndGrantsNothing()
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
            OwnerRoute(siteId), new OwnerDownloadBlockExemptionEndpoints.SetDownloadBlockExemptionRequest(Exempt: true, Reason: "   "));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var verifyDb = fixture.CreateDbContext();
        var site = await new SiteRepository(verifyDb).GetByIdAsync(siteId, CancellationToken.None);
        Assert.False(site!.DownloadBlockExempt);
    }

    // ------------------------------------------------------------------------------------------
    // The authorization boundary: this route is RequirePlatformOwner and nothing weaker - the
    // identical three cases OwnerModuleEndpointsTests/OwnerTenantIsolationEndpointTests already prove
    // for their own owner-only routes.
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task OrdinaryOperatorToken_IsRejected()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, await fixture.GetDemoOperatorAccessTokenAsync());

        var response = await client.PostAsJsonAsync(
            OwnerRoute(new SiteId(Guid.NewGuid())),
            new OwnerDownloadBlockExemptionEndpoints.SetDownloadBlockExemptionRequest(Exempt: true, Reason: "attempted"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>The case that matters most, the identical lesson every other owner-only route in this
    /// codebase proves for itself: a site-wide `"Admin"`, holding every permission that exists for
    /// their own tenant, is still not the platform owner.</summary>
    [Fact]
    public async Task SiteConfigureHoldingAdminToken_IsRejected()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, await fixture.GetDemoAdminAccessTokenAsync());

        var response = await client.PostAsJsonAsync(
            OwnerRoute(new SiteId(Guid.NewGuid())),
            new OwnerDownloadBlockExemptionEndpoints.SetDownloadBlockExemptionRequest(Exempt: true, Reason: "attempted"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NoToken_IsRejected()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token: null);

        var response = await client.PostAsJsonAsync(
            OwnerRoute(new SiteId(Guid.NewGuid())),
            new OwnerDownloadBlockExemptionEndpoints.SetDownloadBlockExemptionRequest(Exempt: true, Reason: "attempted"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>A fresh site, tier and egress row, seeded so the real
    /// <see cref="GetAttachmentDownloadUrlHandler"/> gate genuinely refuses before any exemption is
    /// granted - the same real-Postgres setup shape
    /// <see cref="SiteAttachmentStorageHandlersTests"/>'s own hard-threshold tests use, restated here
    /// because this file needs the gate to already be tripped before its own HTTP writes begin.</summary>
    private async Task<(SiteId SiteId, ConversationId ConversationId, VisitorId VisitorId, AttachmentId AttachmentId)> SeedBlockedSiteAsync()
    {
        var tier = $"test_{Guid.NewGuid():N}";
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO tier_download_thresholds (tier, soft_threshold_bytes, hard_threshold_bytes, updated_at, updated_by)
                VALUES (@tier, @soft, @hard, now(), 'OwnerDownloadBlockExemptionEndpointTests')
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
                attachmentId, siteId, conversationId, $"site/{siteId.Value:N}/{attachmentId.Value:N}.png", "image/png", 50, Now);
            attachment.ConfirmReady(50, "image/png", Now);
            db.Attachments.Add(attachment);
            await db.SaveChangesAsync();
        }

        await new AttachmentEgressMeterStore(fixture.DataSource).RecordAsync(
            siteId, new DateOnly(Now.Year, Now.Month, 1), bytes: 200, CancellationToken.None);

        return (siteId, conversationId, visitorId, attachmentId);
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

    /// <summary>The production wiring for this one owner-only route - the same "registered one by one
    /// against the fixture's own already-built data source" shape
    /// <see cref="CrossTenantRouteIsolationTests"/>'s own remarks give in full for the identical
    /// reason (<c>NpgsqlDataSource.ConnectionString</c> redacts the password).</summary>
    private async Task<WebApplication> BuildTestHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();

        builder.Services.AddSingleton(fixture.DataSource);
        builder.Services.AddDbContext<AgoChatDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>()));
        builder.Services.AddScoped<ISiteRepository, SiteRepository>();
        builder.Services.AddScoped<SetDownloadBlockExemptionAsOwnerHandler>();
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
        app.MapOwnerDownloadBlockExemptionEndpoints();

        await app.StartAsync();
        return app;
    }
}
