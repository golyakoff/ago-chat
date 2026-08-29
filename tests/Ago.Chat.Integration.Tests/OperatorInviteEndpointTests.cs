using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.OperatorInvites;
using Ago.Chat.Api.Sites;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.CreateOperatorInvite;
using Ago.Chat.Application.UseCases.GetMessageArchiveDownloadUrl;
using Ago.Chat.Application.UseCases.GetSiteExportStatus;
using Ago.Chat.Application.UseCases.ListMessageArchives;
using Ago.Chat.Application.UseCases.RedeemOperatorInvite;
using Ago.Chat.Application.UseCases.RegisterSite;
using Ago.Chat.Application.UseCases.RequestSiteExport;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
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
/// `13-01`'s own Done-when, against a real Keycloak and a real Postgres
/// (<see cref="OperatorOidcFixture"/>), the same shape <see cref="SiteRegistrationTests"/> already
/// established for `10-02`'s own bootstrap endpoint - a real admin generates an invite, a real
/// Keycloak-signed token with no matching `operators` row redeems it and gets a working operator
/// session, every rejection is proven with a real second call rather than asserted from the handler's
/// logic alone.
///
/// Every test registers its own fresh `Site` through the real `POST /api/v1/sites` endpoint (never
/// <see cref="OperatorOidcFixture.SeededSiteId"/>, which already carries two operators against the
/// default `seat_limit` of `1` - using it here would make every redemption seat-limited before the
/// test's own subject even begins) - `RegisterSiteHandler` grants the registering operator *both*
/// built-in roles (`5-08`'s own shape), so that operator already holds `Permission.SiteManageOperators`
/// and needs no separate admin-seeding step to generate an invite.
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class OperatorInviteEndpointTests(OperatorOidcFixture fixture)
{
    [Fact]
    public async Task Redeem_ARealKeycloakTokenWithNoOperatorRowAnywhere_BecomesAWorkingOperatorOfTheInvitingSite()
    {
        await using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();

        var (adminSite, adminOperatorId, adminToken) = await RegisterFreshSiteAsync(client);
        await RaiseSeatLimitAsync(adminSite, seatLimit: 2);

        var invite = await CreateInviteAsync(client, adminToken, adminSite, "Operator");

        var (redeemerToken, redeemerUsername) = await fixture.CreateFreshUserAccessTokenAsync();
        using var redeemClient = host.GetTestClient();
        redeemClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", redeemerToken);
        var redeemResponse = await redeemClient.PostAsJsonAsync(
            "/api/v1/operator-invites/redeem", new OperatorInviteEndpoints.RedeemOperatorInviteRequest(invite.Code));

        Assert.Equal(HttpStatusCode.OK, redeemResponse.StatusCode);
        var redeemed = await redeemResponse.Content.ReadFromJsonAsync<OperatorInviteEndpoints.RedeemOperatorInviteResponse>();
        Assert.NotNull(redeemed);
        Assert.Equal(adminSite, redeemed.SiteId);

        // Queried directly, not just asserted from the 200 - this item's own Done-when.
        await using var db = fixture.CreateDbContext();
        var operatorRow = await db.Operators.SingleAsync(o => o.Id == new OperatorId(redeemed.OperatorId));
        Assert.Equal(new SiteId(adminSite), operatorRow.SiteId);

        var operatorRoleId = await db.Roles
            .Where(r => r.SiteId == new SiteId(adminSite) && r.Name == "Operator")
            .Select(r => r.Id)
            .SingleAsync();
        var grantedRoleIds = await db.OperatorRoles
            .Where(or => or.OperatorId == operatorRow.Id)
            .Select(or => or.RoleId)
            .ToListAsync();
        Assert.Equal([operatorRoleId], grantedRoleIds);

        var inviteRow = await db.OperatorInvites.SingleAsync(i => i.Id == new OperatorInviteId(invite.OperatorInviteId));
        Assert.True(inviteRow.IsRedeemed);
        Assert.Equal(operatorRow.Id, inviteRow.RedeemedByOperatorId);

        // `13-01`'s own Done-when: the redeemed identity works as a real operator, proven against a
        // *second*, freshly re-fetched token for the same identity - SiteRegistrationTests' own
        // precedent for why this must be a second token, not the one used to redeem.
        var operatorToken = await fixture.RefreshAccessTokenAsync(redeemerUsername);
        using var operatorClient = host.GetTestClient();
        operatorClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", operatorToken);
        var operatorOnlyResponse = await operatorClient.GetAsync("/operator-only");
        Assert.Equal(HttpStatusCode.OK, operatorOnlyResponse.StatusCode);
    }

    [Fact]
    public async Task Redeem_ASecondRedemptionOfTheSameCode_IsRejectedAlreadyRedeemed()
    {
        await using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();

        var (adminSite, _, adminToken) = await RegisterFreshSiteAsync(client);
        await RaiseSeatLimitAsync(adminSite, seatLimit: 3);
        var invite = await CreateInviteAsync(client, adminToken, adminSite, "Operator");

        var (firstRedeemerToken, _) = await fixture.CreateFreshUserAccessTokenAsync();
        using var firstRedeemer = host.GetTestClient();
        firstRedeemer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", firstRedeemerToken);
        var first = await firstRedeemer.PostAsJsonAsync(
            "/api/v1/operator-invites/redeem", new OperatorInviteEndpoints.RedeemOperatorInviteRequest(invite.Code));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // A second, different fresh identity - the code is already spent regardless of who presents it.
        var (secondRedeemerToken, _) = await fixture.CreateFreshUserAccessTokenAsync();
        using var secondRedeemer = host.GetTestClient();
        secondRedeemer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secondRedeemerToken);
        var second = await secondRedeemer.PostAsJsonAsync(
            "/api/v1/operator-invites/redeem", new OperatorInviteEndpoints.RedeemOperatorInviteRequest(invite.Code));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Redeem_AnExpiredInvite_IsRejectedGone()
    {
        await using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();

        var (adminSite, _, adminToken) = await RegisterFreshSiteAsync(client);
        await RaiseSeatLimitAsync(adminSite, seatLimit: 2);
        var invite = await CreateInviteAsync(client, adminToken, adminSite, "Operator");

        await using (var db = fixture.CreateDbContext())
        {
            var inviteRow = await db.OperatorInvites.SingleAsync(i => i.Id == new OperatorInviteId(invite.OperatorInviteId));
            db.Entry(inviteRow).Property("ExpiresAt").CurrentValue = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var (redeemerToken, _) = await fixture.CreateFreshUserAccessTokenAsync();
        using var redeemClient = host.GetTestClient();
        redeemClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", redeemerToken);
        var response = await redeemClient.PostAsJsonAsync(
            "/api/v1/operator-invites/redeem", new OperatorInviteEndpoints.RedeemOperatorInviteRequest(invite.Code));

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
    }

    [Fact]
    public async Task Redeem_ANonExistentCode_IsRejectedNotFound()
    {
        await using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();

        var (redeemerToken, _) = await fixture.CreateFreshUserAccessTokenAsync();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", redeemerToken);
        var response = await client.PostAsJsonAsync(
            "/api/v1/operator-invites/redeem", new OperatorInviteEndpoints.RedeemOperatorInviteRequest("invite_does-not-exist"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// `13-07`/`adr/0068`'s own adjustment to this item's originally-scoped check, proven rather than
    /// asserted: the redeeming identity already resolves to an `Operator` row on *this invite's own*
    /// site is rejected `409` - the older, superseded "resolves to an operator row anywhere" rule this
    /// item's backlog note was corrected away from once `13-07` shipped.
    /// </summary>
    [Fact]
    public async Task Redeem_FromASubThatAlreadyAdministersThisSite_IsRejectedConflict()
    {
        await using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();

        var (adminSite, _, adminToken) = await RegisterFreshSiteAsync(client);
        await RaiseSeatLimitAsync(adminSite, seatLimit: 5);
        var invite = await CreateInviteAsync(client, adminToken, adminSite, "Operator");

        // The site's own registering identity - already an Operator (and Admin) of this exact site -
        // tries to redeem a second invite for the same site.
        using var sameAdminClient = host.GetTestClient();
        sameAdminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var response = await sameAdminClient.PostAsJsonAsync(
            "/api/v1/operator-invites/redeem", new OperatorInviteEndpoints.RedeemOperatorInviteRequest(invite.Code));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    /// <summary>
    /// The `13-07` contrast case, proven so the adjustment above cannot silently regress into the
    /// older rule: the identical identity already administers a *different* site and must still be
    /// allowed to redeem an invite for this one - `OperatorInviteRedemptionResult.AlreadyOperatorOnSite`
    /// is scoped to `invite.SiteId` specifically, never to the identity as a whole.
    /// </summary>
    [Fact]
    public async Task Redeem_FromASubThatAdministersADifferentSite_Succeeds()
    {
        await using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();

        var (adminSite, _, adminToken) = await RegisterFreshSiteAsync(client);
        await RaiseSeatLimitAsync(adminSite, seatLimit: 5);
        var invite = await CreateInviteAsync(client, adminToken, adminSite, "Operator");

        // A second, unrelated identity that already administers its own, different site.
        using var otherSiteClient = host.GetTestClient();
        var (_, _, otherAdminToken) = await RegisterFreshSiteAsync(otherSiteClient);

        using var redeemClient = host.GetTestClient();
        redeemClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", otherAdminToken);
        var response = await redeemClient.PostAsJsonAsync(
            "/api/v1/operator-invites/redeem", new OperatorInviteEndpoints.RedeemOperatorInviteRequest(invite.Code));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// `13-01`'s own Done-when: "a capacity-rejected invite is confirmed still redeemable afterward
    /// once a seat opens up... proving the invite was not silently consumed by the rejected attempt."
    /// A freshly registered site's own `seat_limit` defaults to `1` and already carries its own
    /// registering operator, so the very first redemption attempt is rejected on capacity with no setup
    /// needed beyond registering the site.
    /// </summary>
    [Fact]
    public async Task Redeem_WhenTheSiteIsAtItsSeatLimit_IsRejected402AndTheInviteStaysRedeemableAfterASeatOpens()
    {
        await using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();

        var (adminSite, _, adminToken) = await RegisterFreshSiteAsync(client);
        // seat_limit stays at its default of 1 - the registering operator alone already fills it.
        var invite = await CreateInviteAsync(client, adminToken, adminSite, "Operator");

        // The identical identity presents the identical code both times - this test is about the
        // invite's own redeemability surviving a rejection, not about who holds the code.
        var (redeemerToken, _) = await fixture.CreateFreshUserAccessTokenAsync();

        using var firstAttemptClient = host.GetTestClient();
        firstAttemptClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", redeemerToken);
        var rejected = await firstAttemptClient.PostAsJsonAsync(
            "/api/v1/operator-invites/redeem", new OperatorInviteEndpoints.RedeemOperatorInviteRequest(invite.Code));
        Assert.Equal(HttpStatusCode.PaymentRequired, rejected.StatusCode);

        await using (var db = fixture.CreateDbContext())
        {
            var inviteRow = await db.OperatorInvites.AsNoTracking().SingleAsync(i => i.Id == new OperatorInviteId(invite.OperatorInviteId));
            Assert.False(inviteRow.IsRedeemed);
        }

        await RaiseSeatLimitAsync(adminSite, seatLimit: 2);

        using var secondAttemptClient = host.GetTestClient();
        secondAttemptClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", redeemerToken);
        var succeeded = await secondAttemptClient.PostAsJsonAsync(
            "/api/v1/operator-invites/redeem", new OperatorInviteEndpoints.RedeemOperatorInviteRequest(invite.Code));
        Assert.Equal(HttpStatusCode.OK, succeeded.StatusCode);

        await using (var db = fixture.CreateDbContext())
        {
            var inviteRow = await db.OperatorInvites.AsNoTracking().SingleAsync(i => i.Id == new OperatorInviteId(invite.OperatorInviteId));
            Assert.True(inviteRow.IsRedeemed);
        }
    }

    [Fact]
    public async Task CreateInvite_WhenTheCallerLacksSiteManageOperators_IsRejectedForbidden()
    {
        await using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();

        // demo-operator holds "Operator" only, never "Admin" - OperatorOidcFixture's own seeding.
        var operatorOnlyToken = await fixture.GetDemoOperatorAccessTokenAsync();
        using var operatorClient = host.GetTestClient();
        operatorClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", operatorOnlyToken);

        var response = await operatorClient.PostAsJsonAsync(
            $"/api/v1/sites/{fixture.SeededSiteId.Value}/operator-invites",
            new OperatorInviteEndpoints.CreateOperatorInviteRequest("Operator"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<(Guid SiteId, Guid OperatorId, string Token)> RegisterFreshSiteAsync(HttpClient client)
    {
        var (token, _) = await fixture.CreateFreshUserAccessTokenAsync();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync(
            "/api/v1/sites", new SitesEndpoints.RegisterSiteRequest("Acme Support", "https://shop.example.com"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SitesEndpoints.RegisterSiteResponse>();
        Assert.NotNull(body);

        return (body.SiteId, body.OperatorId, token);
    }

    private async Task<OperatorInviteEndpoints.CreateOperatorInviteResponse> CreateInviteAsync(
        HttpClient client, string adminToken, Guid siteId, string roleName)
    {
        using var adminClient = client;
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var response = await adminClient.PostAsJsonAsync(
            $"/api/v1/sites/{siteId}/operator-invites", new OperatorInviteEndpoints.CreateOperatorInviteRequest(roleName));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OperatorInviteEndpoints.CreateOperatorInviteResponse>();
        Assert.NotNull(body);
        return body;
    }

    /// <summary>Simulates `13-02`'s own not-yet-built "raise a site's tier/seat_limit" surface -
    /// this item's own Out of scope names that as a separate item's job, so tests that need a seat to
    /// redeem into write the column directly, matching this item's own Done-when's suggested technique
    /// ("`seat_limit` is raised").</summary>
    private async Task RaiseSeatLimitAsync(Guid siteId, int seatLimit)
    {
        await using var db = fixture.CreateDbContext();
        var site = await db.Sites.SingleAsync(s => s.Id == new SiteId(siteId));
        db.Entry(site).Property(nameof(Site.SeatLimit)).CurrentValue = seatLimit;
        await db.SaveChangesAsync();
    }

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
        builder.Services.AddScoped<IPermissionChecker, PermissionChecker>();
        builder.Services.AddScoped<IRoleRepository, RoleRepository>();
        builder.Services.AddScoped<IOperatorInviteRepository, OperatorInviteRepository>();
        builder.Services.AddScoped<IOperatorInviteRedemptionRepository, OperatorInviteRedemptionRepository>();
        builder.Services.AddSingleton<IOperatorInviteCodeGenerator, OperatorInviteCodeGenerator>();
        builder.Services.AddSingleton(new OperatorInviteOptions());
        builder.Services.AddScoped<ResolveOperatorIdentityHandler>();
        builder.Services.AddScoped<RegisterSiteHandler>();
        builder.Services.AddScoped<CreateOperatorInviteHandler>();
        builder.Services.AddScoped<RedeemOperatorInviteHandler>();
        // `16-03`: SitesEndpoints now also maps the export routes - see SiteRegistrationTests'
        // own remarks (this file's own precedent for a stripped-down host). IPermissionChecker is
        // already registered above.
        builder.Services.AddScoped<IExportRequestRepository, ExportRequestRepository>();
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
        app.MapOperatorInviteEndpoints();
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
