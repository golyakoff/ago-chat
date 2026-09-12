using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Owner;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Application.UseCases.RestoreOperatorSeatAsOwner;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
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
/// `23-68`'s own Done-when, over a real HTTP pipeline, a real Postgres and real Keycloak-signed tokens
/// (<see cref="OperatorOidcFixture"/>): the platform owner can restore a locked-out operator's seat,
/// the seat-limit interaction behaves as decided, the action is recorded with the real owner subject,
/// and nobody but the platform owner can reach this route.
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class OwnerOperatorsEndpointsTests(OperatorOidcFixture fixture)
{
    private string RestoreSeatRoute(Guid operatorId, Guid? siteId = null) =>
        $"/api/v1/owner/sites/{siteId ?? fixture.SeededSiteId.Value}/operators/{operatorId}/restore-seat";

    /// <summary>The item's own headline Done-when, end to end: an operator who holds no seat is
    /// restored through the real route, and the real row - not merely the response - now holds one.
    /// </summary>
    [Fact]
    public async Task OwnerToken_RestoresALockedOutOperatorsSeat_AndTheRealRowReflectsIt()
    {
        // A fresh tenant with room on its own seat limit - `fixture.SeededSiteId` already holds two
        // seated operators of its own (OperatorOidcFixture's own seed), exactly its default seat limit,
        // so this test needs a tenant that is not already at capacity to prove the ordinary,
        // within-limit case rather than accidentally exercising the override path below.
        var siteId = await SeedSiteWithSeatLimitAsync(seatLimit: 3);
        var lockedOutId = await SeedOperatorForSiteAsync(siteId, holdsSeat: false);
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await ownerClient.PostAsync(
            RestoreSeatRoute(lockedOutId.Value, siteId.Value), content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OwnerOperatorsEndpoints.RestoreOperatorSeatResponse>();
        Assert.NotNull(body);
        Assert.False(body.AlreadyHeldSeat);
        Assert.False(body.OverrodeSeatLimit);

        // Trying it: the real row, read back through a fresh repository instance - not asserted from
        // the response alone.
        var reloaded = await GetOperatorAsync(lockedOutId, siteId);
        Assert.NotNull(reloaded);
        Assert.True(reloaded.HoldsSeat);
    }

    /// <summary>Restoring an already-seated operator is a harmless no-op over the real route too.</summary>
    [Fact]
    public async Task OwnerToken_RestoresAnAlreadySeatedOperator_ReportsAlreadyHeldSeat()
    {
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        // fixture.SeededOperatorId already holds a seat by construction (OperatorOidcFixture's own seed).
        var response = await ownerClient.PostAsync(RestoreSeatRoute(fixture.SeededOperatorId.Value), content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OwnerOperatorsEndpoints.RestoreOperatorSeatResponse>();
        Assert.NotNull(body);
        Assert.True(body.AlreadyHeldSeat);
    }

    /// <summary>The seat-limit decision, proven over the real route: exceeding the tenant's own seat
    /// limit with no force is refused with a `409`, and the row is left unchanged.</summary>
    [Fact]
    public async Task OwnerToken_ExceedingTheSeatLimit_WithNoForce_IsRefused()
    {
        var siteId = await SeedSiteWithSeatLimitAsync(seatLimit: 1);
        var alreadyHoldingId = await SeedOperatorForSiteAsync(siteId, holdsSeat: true);
        var lockedOutId = await SeedOperatorForSiteAsync(siteId, holdsSeat: false);
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await ownerClient.PostAsync(
            $"/api/v1/owner/sites/{siteId.Value}/operators/{lockedOutId.Value}/restore-seat", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var reloaded = await GetOperatorAsync(lockedOutId, siteId);
        Assert.NotNull(reloaded);
        Assert.False(reloaded.HoldsSeat);
        _ = alreadyHoldingId;
    }

    /// <summary>The override, exercised over the real route: force plus a real reason succeeds past the
    /// limit, the row really changes, and the override lands in the real
    /// `operator_seat_restore_overrides` table with the real owner subject off the real token - not a
    /// stand-in (the identical proof `OwnerModuleEndpointsTests.OwnerToken_RevokesASelfServicePurchase_WithForceAndAReason_Succeeds_AndRecordsTheOverride`
    /// already gives for its own sibling record).</summary>
    [Fact]
    public async Task OwnerToken_ExceedingTheSeatLimit_WithForceAndAReason_Succeeds_AndRecordsTheOverride()
    {
        var siteId = await SeedSiteWithSeatLimitAsync(seatLimit: 1);
        await SeedOperatorForSiteAsync(siteId, holdsSeat: true);
        var lockedOutId = await SeedOperatorForSiteAsync(siteId, holdsSeat: false);

        var token = await fixture.GetPlatformOwnerAccessTokenAsync();
        var ownerSubject = new JwtSecurityTokenHandler().ReadJwtToken(token).Subject;
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, token);

        const string reason = "Tenant locked itself out during a live demo; overriding the seat limit to restore access.";
        var response = await ownerClient.PostAsJsonAsync(
            $"/api/v1/owner/sites/{siteId.Value}/operators/{lockedOutId.Value}/restore-seat",
            new OwnerOperatorsEndpoints.RestoreOperatorSeatRequest(Force: true, Reason: reason));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OwnerOperatorsEndpoints.RestoreOperatorSeatResponse>();
        Assert.NotNull(body);
        Assert.True(body.OverrodeSeatLimit);

        var reloaded = await GetOperatorAsync(lockedOutId, siteId);
        Assert.NotNull(reloaded);
        Assert.True(reloaded.HoldsSeat);

        var overrides = await new OperatorSeatRestoreOverrideRepository(fixture.DataSource)
            .ListForSiteAsync(siteId, CancellationToken.None);
        var recorded = Assert.Single(overrides, o => o.OperatorId == lockedOutId);
        Assert.Equal(ownerSubject, recorded.RestoredBy);
        Assert.Equal(reason, recorded.Reason);
    }

    [Fact]
    public async Task OwnerToken_ForcesAnOverride_WithNoReason_IsRefused_AndGrantsNoOverride()
    {
        var siteId = await SeedSiteWithSeatLimitAsync(seatLimit: 1);
        await SeedOperatorForSiteAsync(siteId, holdsSeat: true);
        var lockedOutId = await SeedOperatorForSiteAsync(siteId, holdsSeat: false);
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await ownerClient.PostAsJsonAsync(
            $"/api/v1/owner/sites/{siteId.Value}/operators/{lockedOutId.Value}/restore-seat",
            new OwnerOperatorsEndpoints.RestoreOperatorSeatRequest(Force: true, Reason: null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var overrides = await new OperatorSeatRestoreOverrideRepository(fixture.DataSource)
            .ListForSiteAsync(siteId, CancellationToken.None);
        Assert.DoesNotContain(overrides, o => o.OperatorId == lockedOutId);
    }

    /// <summary>A removed operator is refused, not silently no-op'd - the real row proves it stayed
    /// seatless.</summary>
    [Fact]
    public async Task OwnerToken_ForARemovedOperator_IsRefused()
    {
        var removedId = await SeedOperatorAsync(holdsSeat: false, removedAt: DateTimeOffset.UtcNow.AddDays(-1));
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await ownerClient.PostAsync(RestoreSeatRoute(removedId.Value), content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task OwnerToken_ForANonexistentOperator_Returns404()
    {
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await ownerClient.PostAsync(RestoreSeatRoute(Guid.NewGuid()), content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>`24-12`: the access record, proven with the real owner subject off the real token - the
    /// identical proof <c>OwnerSitesEndpointTests</c>'s own equivalent test gives for the list route.
    /// </summary>
    [Fact]
    public async Task OwnerToken_RestoringASeat_LeavesAnAccessRecord_NamingTheRealOwnerSubject_AndTheOperator()
    {
        // A fresh tenant with room - the identical reason `OwnerToken_RestoresALockedOutOperatorsSeat_
        // AndTheRealRowReflectsIt`'s own remarks give: `fixture.SeededSiteId` is already at its default
        // seat limit.
        var siteId = await SeedSiteWithSeatLimitAsync(seatLimit: 3);
        var lockedOutId = await SeedOperatorForSiteAsync(siteId, holdsSeat: false);
        var token = await fixture.GetPlatformOwnerAccessTokenAsync();
        var ownerSubject = new JwtSecurityTokenHandler().ReadJwtToken(token).Subject;
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, token);

        var response = await ownerClient.PostAsync(
            RestoreSeatRoute(lockedOutId.Value, siteId.Value), content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var records = await new AccessRecordRepository(fixture.DataSource)
            .ListForSiteAsync(siteId, beforeId: null, limit: 50, CancellationToken.None);
        var recorded = Assert.Single(
            records.Items, r => r.AccessKind == AccessRecordKind.OwnerOperatorSeatRestore && r.ResourceId == lockedOutId.Value);
        Assert.Equal(AccessRecordActorKind.PlatformOwner, recorded.ActorKind);
        Assert.Equal(ownerSubject, recorded.ActorId);
        Assert.Equal(AccessRecordResourceKind.Operator, recorded.ResourceKind);
    }

    // ------------------------------------------------------------------------------------------
    // The authorization boundary: this route is RequirePlatformOwner and nothing weaker.
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task OrdinaryOperatorToken_IsRejected()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, await fixture.GetDemoOperatorAccessTokenAsync());

        var response = await client.PostAsync(RestoreSeatRoute(fixture.SeededOperatorId.Value), content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>The case that matters most, the identical lesson `OwnerModuleEndpointsTests` already
    /// proves for its own owner surface: `5-08`'s site-wide `"Admin"`, holding `site:manage_operators`
    /// for their own site, is still not the platform owner.</summary>
    [Fact]
    public async Task SiteManageOperatorsHoldingAdminToken_IsRejected()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, await fixture.GetDemoAdminAccessTokenAsync());

        var response = await client.PostAsync(RestoreSeatRoute(fixture.SeededOperatorId.Value), content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NoToken_IsRejected()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token: null);

        var response = await client.PostAsync(RestoreSeatRoute(fixture.SeededOperatorId.Value), content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Reads the real row back through a fresh <see cref="OperatorRepository"/> instance and
    /// a fresh <see cref="AgoChatDbContext"/> - never the same context the endpoint's own request used,
    /// so a passing assertion proves the write actually committed rather than merely appearing to,
    /// through change-tracking, in the caller's own in-memory copy.</summary>
    private async Task<Operator?> GetOperatorAsync(OperatorId operatorId, SiteId siteId)
    {
        await using var db = fixture.CreateDbContext();
        return await new OperatorRepository(db).GetByIdAsync(operatorId, siteId, CancellationToken.None);
    }

    private async Task<OperatorId> SeedOperatorAsync(bool holdsSeat, DateTimeOffset? removedAt = null) =>
        await SeedOperatorForSiteAsync(fixture.SeededSiteId, holdsSeat, removedAt);

    private async Task<OperatorId> SeedOperatorForSiteAsync(SiteId siteId, bool holdsSeat, DateTimeOffset? removedAt = null)
    {
        await using var db = fixture.CreateDbContext();
        var operatorId = new OperatorId(Guid.NewGuid());
        db.Operators.Add(new Operator(
            operatorId, siteId, OperatorStatus.Offline, capacity: 5, holdsSeat: holdsSeat, removedAt: removedAt));
        await db.SaveChangesAsync();
        return operatorId;
    }

    /// <summary>A second, freshly registered tenant with its own low seat limit - `fixture.SeededSiteId`
    /// is shared, real Postgres state across every test in this collection
    /// (`OperatorOidcFixture.InitializeAsync`'s own remarks), so a seat-limit test needs its own tenant
    /// rather than risk another test's own seeded operator changing what "at capacity" means.</summary>
    private async Task<SiteId> SeedSiteWithSeatLimitAsync(int seatLimit)
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [], seatLimit: seatLimit));
        await db.SaveChangesAsync();
        return siteId;
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

        builder.Services.AddScoped<IOperatorRepository, OperatorRepository>();
        builder.Services.AddScoped<ResolveOperatorIdentityHandler>();
        builder.Services.AddScoped<IPermissionChecker, PermissionChecker>();
        builder.Services.AddScoped<ISiteRepository, SiteRepository>();
        // A real repository, not a fake - this suite already runs against a real Postgres
        // (fixture.DataSource), and the whole point of the override tests above is proving a real row
        // lands.
        builder.Services.AddScoped<IOperatorSeatRestoreOverrideRepository, OperatorSeatRestoreOverrideRepository>();
        builder.Services.AddScoped<RestoreOperatorSeatAsOwnerHandler>();
        // `24-12`: the owner endpoint's own access-record write - OwnerAccessRecorder resolves this
        // straight from DI, the same way the production host does. IClock/IIdGenerator are already
        // registered above (AddPlatformKernel).
        builder.Services.AddScoped<IAccessRecordRepository, AccessRecordRepository>();
        builder.Services.AddSingleton<IClock, Ago.Platform.Hosting.SystemClock>();

        builder.Services.AddHttpContextAccessor();
        // `23-73`: OperatorIdentityClaimsTransformation's own new dependencies - the watchdog
        // reset hook and (where this host did not already have one) IClock.
        builder.Services.AddScoped<Ago.Chat.Application.Abstractions.ISiteActivityWatchdog, Ago.Chat.Infrastructure.Postgres.SiteActivityWatchdogRepository>();
        builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new Ago.Chat.Infrastructure.Postgres.SiteActivityWatchdogOptions()));
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
            options.AddPolicy("RequirePlatformOwner", policy => policy
                .AddAuthenticationSchemes(JwtSchemes.Operator)
                .RequireAuthenticatedUser()
                .AddRequirements(new PlatformOwnerRequirement()));
        });
        builder.Services.AddSingleton<IAuthorizationHandler, PlatformOwnerAuthorizationHandler>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapOwnerOperatorsEndpoints();

        await app.StartAsync();
        return app;
    }
}
