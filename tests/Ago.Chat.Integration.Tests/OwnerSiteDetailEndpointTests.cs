using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Owner;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetSiteForOwner;
using Ago.Chat.Application.UseCases.ListSitesForOwner;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
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
/// `23-14`'s own Done-when, against a real Keycloak and a real Postgres
/// (<see cref="OperatorOidcFixture"/>): `GET /api/v1/owner/sites/{siteId}`, the platform owner's
/// per-tenant detail read - who may call it, what it returns for a tenant with real entitlements, and
/// what it does for one that does not exist.
///
/// <para>Runs the production <c>OwnerSitesEndpoints.MapOwnerEndpoints</c>/
/// <c>GetSiteForOwnerHandler</c>/<c>PlatformOverviewReadStore</c>/<c>EnabledModuleReadStore</c> against
/// a minimal <see cref="TestServer"/>, the same seam <see cref="OwnerSitesEndpointTests"/> already
/// established for this route file - deliberately a separate test class rather than more methods on
/// that one, since this route's own failure mode (a named site that does not exist) and its own
/// distinguishing claim (entitlements, not just the eight aggregate facts) are a different story from
/// the list's search and pagination.</para>
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class OwnerSiteDetailEndpointTests(OperatorOidcFixture fixture)
{
    private const string RouteBase = "/api/v1/owner/sites";

    [Fact]
    public async Task OwnerToken_GetsTheSiteDetail_WithGroundTruthNumbers()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var createdAt = DateTimeOffset.UtcNow.AddDays(-7);
        await SeedBareTenantAsync(siteId, "Detail Read Tenant", createdAt, ["https://shop.example"]);

        var token = await fixture.GetPlatformOwnerAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var response = await client.GetAsync($"{RouteBase}/{siteId.Value}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OwnerSiteDetailResponse>();
        Assert.NotNull(body);
        Assert.Equal(siteId.Value, body.SiteId);
        Assert.Equal("Detail Read Tenant", body.Name);
        Assert.Equal("free", body.Tier);
        Assert.NotNull(body.CreatedAt);
        Assert.True((body.CreatedAt.Value - createdAt).Duration() < TimeSpan.FromMilliseconds(1));
        Assert.Equal(ListSitesForOwnerHandler.RecentWindowDays, body.RecentWindowDays);
        Assert.Empty(body.Modules);
        // `23-48`: the detail read's own new field - loaded off the write-side aggregate, not the
        // read-model row (GetSiteForOwnerHandler's own remarks).
        Assert.Equal(["https://shop.example"], body.AllowedOrigins);
    }

    /// <summary>The detail read's own reason to exist beyond the list: a module the platform owner
    /// granted, with its expiry, distinguishable from one the tenant enabled themselves.</summary>
    [Fact]
    public async Task OwnerToken_SeesAModuleTheOwnerGranted_DistinguishableFromATenantGrantedOne_WithExpiry()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await SeedBareTenantAsync(siteId, "Entitlements Tenant", DateTimeOffset.UtcNow);

        var now = DateTimeOffset.UtcNow;
        var ownerExpiry = now.AddDays(30);
        await SeedModuleAsync(siteId, "calendar", grantedByOwner: true, enabledAt: now, expiresAt: ownerExpiry);
        await SeedModuleAsync(siteId, "faq", grantedByOwner: false, enabledAt: now, expiresAt: null);

        var token = await fixture.GetPlatformOwnerAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var body = await GetDetailAsync(client, siteId.Value);

        var ownerGranted = Assert.Single(body.Modules, m => m.ModuleKey == "calendar");
        Assert.True(ownerGranted.GrantedByOwner);
        Assert.NotNull(ownerGranted.ExpiresAt);
        Assert.True((ownerGranted.ExpiresAt.Value - ownerExpiry).Duration() < TimeSpan.FromMilliseconds(1));
        Assert.Equal("Active", ownerGranted.Status);

        var tenantGranted = Assert.Single(body.Modules, m => m.ModuleKey == "faq");
        Assert.False(tenantGranted.GrantedByOwner);
        // A grant with no expiry renders as an explicit "no end date" on the console
        // (OwnerSiteDetailPage) - at the wire level that is simply a null the console must not treat
        // as a blank cell, which is this assertion's job to guard.
        Assert.Null(tenantGranted.ExpiresAt);
        Assert.Equal("Active", tenantGranted.Status);
    }

    /// <summary>The Done-when the item's author was most explicit about: an expired grant is shown as
    /// expired, using the live read-store's own `expires_at > now` decision - not omitted, and not
    /// left for the console to work out by comparing dates itself.</summary>
    [Fact]
    public async Task OwnerToken_AnExpiredGrant_IsShownAsExpired_NotOmitted()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await SeedBareTenantAsync(siteId, "Lapsed Trial Tenant", DateTimeOffset.UtcNow);

        var enabledAt = DateTimeOffset.UtcNow.AddDays(-30);
        var expiresAt = DateTimeOffset.UtcNow.AddDays(-5);
        await SeedModuleAsync(siteId, "calendar", grantedByOwner: true, enabledAt: enabledAt, expiresAt: expiresAt);

        var token = await fixture.GetPlatformOwnerAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var body = await GetDetailAsync(client, siteId.Value);

        // Still present - the whole point of this read being a diagnostic history rather than
        // `23-01`'s "currently active only" listing (EnabledModuleDetailSummary's own remarks).
        var expired = Assert.Single(body.Modules, m => m.ModuleKey == "calendar");
        Assert.NotNull(expired.ExpiresAt);
        Assert.Equal("Expired", expired.Status);
        Assert.Null(expired.RevokedAt);
    }

    /// <summary>`23-103`'s own fails-before: `docs/backlog/23-103-*.md`'s reported bug, proven
    /// mechanically. A row with <c>revoked_at</c> stamped and <c>expires_at</c> null - exactly the
    /// shape the platform owner's real revoke on the stand produced - must say <c>"Revoked"</c>, never
    /// the pre-item behaviour of collapsing it into the same <c>"Expired"</c>/`IsActive: false` bucket
    /// as a grant that genuinely lapsed. Also proves <see cref="OwnerSiteModuleDto.RevokedAt"/> carries
    /// the actual stamp - "the row says when", not merely a label.</summary>
    [Fact]
    public async Task OwnerToken_ARevokedGrant_IsShownAsRevoked_NotExpired()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await SeedBareTenantAsync(siteId, "Revoked Grant Tenant", DateTimeOffset.UtcNow);

        var enabledAt = DateTimeOffset.UtcNow.AddDays(-10);
        var revokedAt = DateTimeOffset.UtcNow.AddDays(-2);
        // No end date - exactly `docs/backlog/23-103-*.md`'s own reported shape ("Expires: No end
        // date", "Status: Expired" - self-contradicting, since nothing with no end date can expire).
        await SeedModuleAsync(
            siteId, "calendar", grantedByOwner: true, enabledAt: enabledAt, expiresAt: null, revokedAt: revokedAt);

        var token = await fixture.GetPlatformOwnerAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var body = await GetDetailAsync(client, siteId.Value);

        var revoked = Assert.Single(body.Modules, m => m.ModuleKey == "calendar");
        Assert.Null(revoked.ExpiresAt);
        Assert.Equal("Revoked", revoked.Status);
        Assert.NotNull(revoked.RevokedAt);
        Assert.True((revoked.RevokedAt.Value - revokedAt).Duration() < TimeSpan.FromMilliseconds(1));
    }

    /// <summary>The item's own third Done-when box: a grant that is both expired and revoked reports
    /// <c>"Revoked"</c>, not <c>"Expired"</c> - the precedence <see cref="EnabledModuleDetailSummary"/>'s
    /// own remarks state a reason for (the revoke is the more specific, more recent fact), proven here
    /// rather than left to whichever branch a `case` expression happened to list first.</summary>
    [Fact]
    public async Task OwnerToken_AGrantThatIsBothExpiredAndRevoked_ReportsRevoked()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await SeedBareTenantAsync(siteId, "Expired And Revoked Tenant", DateTimeOffset.UtcNow);

        var enabledAt = DateTimeOffset.UtcNow.AddDays(-30);
        var expiresAt = DateTimeOffset.UtcNow.AddDays(-10);
        var revokedAt = DateTimeOffset.UtcNow.AddDays(-2);
        await SeedModuleAsync(
            siteId, "calendar", grantedByOwner: true, enabledAt: enabledAt, expiresAt: expiresAt,
            revokedAt: revokedAt);

        var token = await fixture.GetPlatformOwnerAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var body = await GetDetailAsync(client, siteId.Value);

        var module = Assert.Single(body.Modules, m => m.ModuleKey == "calendar");
        Assert.Equal("Revoked", module.Status);
        Assert.NotNull(module.ExpiresAt);
        Assert.NotNull(module.RevokedAt);
    }

    /// <summary>The item's own third Done-when box, the other half: "what the screen shows after a
    /// revoke-then-re-grant is decided and demonstrated" (`adr/0155`'s own "leaves two rows"). This
    /// contract half's answer is "two rows honestly" - <see cref="GetSiteForOwnerHandler"/> reads
    /// <c>GetAllForSiteAsync</c> unfiltered exactly as it did before this item, so both the tombstoned
    /// original and the fresh grant come back, each correctly labelled and each with its own
    /// <see cref="OwnerSiteModuleDto.Id"/> to tell them apart - what the console does with two rows for
    /// one module is that half's own decision (`docs/backlog/23-103-*.md`'s own "not an obvious
    /// rendering").</summary>
    [Fact]
    public async Task OwnerToken_ARevokeThenReGrant_ReturnsBothRowsDistinguishable()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await SeedBareTenantAsync(siteId, "Re-granted Tenant", DateTimeOffset.UtcNow);

        var firstEnabledAt = DateTimeOffset.UtcNow.AddDays(-20);
        var revokedAt = DateTimeOffset.UtcNow.AddDays(-10);
        await SeedModuleAsync(
            siteId, "calendar", grantedByOwner: true, enabledAt: firstEnabledAt, expiresAt: null,
            revokedAt: revokedAt);

        var secondEnabledAt = DateTimeOffset.UtcNow.AddDays(-1);
        await SeedModuleAsync(siteId, "calendar", grantedByOwner: true, enabledAt: secondEnabledAt, expiresAt: null);

        var token = await fixture.GetPlatformOwnerAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var body = await GetDetailAsync(client, siteId.Value);

        var calendarRows = body.Modules.Where(m => m.ModuleKey == "calendar").ToList();
        Assert.Equal(2, calendarRows.Count);
        // Distinguishable: two different row ids, not the same row counted twice.
        Assert.NotEqual(calendarRows[0].Id, calendarRows[1].Id);

        var revokedRow = Assert.Single(calendarRows, m => m.Status == "Revoked");
        var liveRow = Assert.Single(calendarRows, m => m.Status == "Active");
        Assert.NotNull(revokedRow.RevokedAt);
        Assert.Null(liveRow.RevokedAt);
    }

    /// <summary>`23-66`'s own warning, checked mechanically: a module nobody ever granted a quantity
    /// for reports <see langword="null"/>, not <c>0</c> - the two must never render the same on the
    /// owner's own screen.</summary>
    [Fact]
    public async Task OwnerToken_AModuleWithNoQuantityGrant_ReportsANullQuantity_NotZero()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await SeedBareTenantAsync(siteId, "No Quantity Grant Tenant", DateTimeOffset.UtcNow);
        await SeedModuleAsync(siteId, "calendar", grantedByOwner: true, enabledAt: DateTimeOffset.UtcNow, expiresAt: null);

        var token = await fixture.GetPlatformOwnerAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var body = await GetDetailAsync(client, siteId.Value);

        var module = Assert.Single(body.Modules, m => m.ModuleKey == "calendar");
        Assert.Null(module.Quantity);
    }

    /// <summary>The other half of the same warning: a quantity explicitly granted as zero - a tenant
    /// who has the module and has not created a worker yet - reports <c>0</c>, distinguishable from
    /// the "never granted" case above rather than collapsing into it.</summary>
    [Fact]
    public async Task OwnerToken_AModuleGrantedAZeroQuantity_ReportsZero_NotNull()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        await SeedBareTenantAsync(siteId, "Zero Quantity Tenant", now);
        await SeedModuleAsync(siteId, "calendar", grantedByOwner: true, enabledAt: now, expiresAt: null);
        await SeedQuantityGrantAsync(siteId, "calendar", quantity: 0, now);

        var token = await fixture.GetPlatformOwnerAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var body = await GetDetailAsync(client, siteId.Value);

        var module = Assert.Single(body.Modules, m => m.ModuleKey == "calendar");
        Assert.NotNull(module.Quantity);
        Assert.Equal(0, module.Quantity.Value);
    }

    /// <summary>A quantity granted above zero, the ordinary case - present and equal to what was
    /// granted, proven alongside the two edge cases above rather than assumed from them.</summary>
    [Fact]
    public async Task OwnerToken_AModuleGrantedAQuantity_ReportsIt()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        await SeedBareTenantAsync(siteId, "Quantity Tenant", now);
        await SeedModuleAsync(siteId, "calendar", grantedByOwner: true, enabledAt: now, expiresAt: null);
        await SeedQuantityGrantAsync(siteId, "calendar", quantity: 3, now);

        var token = await fixture.GetPlatformOwnerAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var body = await GetDetailAsync(client, siteId.Value);

        var module = Assert.Single(body.Modules, m => m.ModuleKey == "calendar");
        Assert.Equal(3, module.Quantity);
    }

    [Fact]
    public async Task OwnerToken_ForANonexistentSite_Returns404()
    {
        var token = await fixture.GetPlatformOwnerAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var response = await client.GetAsync($"{RouteBase}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>`23-14`'s own Done-when: a non-owner gets the same refusal the list gives, and no site
    /// data is loaded - proven against a real seeded tenant's own id, so a 403 cannot be mistaken for a
    /// 404 that would have happened anyway.</summary>
    [Fact]
    public async Task OrdinaryOperatorToken_IsRejected_AndNoSiteDataIsLoaded()
    {
        var token = await fixture.GetDemoOperatorAccessTokenAsync();

        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var response = await client.GetAsync($"{RouteBase}/{fixture.SeededSiteId.Value}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>The same site-wide `"Admin"`-is-not-the-platform-owner case
    /// <see cref="OwnerSitesEndpointTests.SiteConfigureHoldingAdminToken_IsRejected"/> proves for the
    /// list - restated here because this route's own access-control story
    /// (<see cref="GetSiteForOwnerHandler"/>'s remarks) is a second, independent claim, not a
    /// consequence of the list's.</summary>
    [Fact]
    public async Task SiteConfigureHoldingAdminToken_IsRejected()
    {
        var token = await fixture.GetDemoAdminAccessTokenAsync();

        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var response = await client.GetAsync($"{RouteBase}/{fixture.SeededSiteId.Value}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NoToken_IsRejected()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token: null);

        var response = await client.GetAsync($"{RouteBase}/{fixture.SeededSiteId.Value}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<OwnerSiteDetailResponse> GetDetailAsync(HttpClient client, Guid siteId)
    {
        var response = await client.GetAsync($"{RouteBase}/{siteId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OwnerSiteDetailResponse>();
        Assert.NotNull(body);
        return body;
    }

    private async Task SeedBareTenantAsync(
        SiteId siteId, string name, DateTimeOffset createdAt, IReadOnlyList<string>? allowedOrigins = null)
    {
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", allowedOrigins ?? [], name, createdAt));
        await db.SaveChangesAsync();
    }

    /// <summary>`23-66`: writes a real <c>module_quantity_grants</c> row through the real store, not a
    /// hand-built entity - the same "seed through the mechanism, not around it" posture
    /// <see cref="SeedModuleAsync"/> already follows for <c>EnabledModule</c>.</summary>
    private async Task SeedQuantityGrantAsync(SiteId siteId, string moduleKey, int quantity, DateTimeOffset now)
    {
        await using var db = fixture.CreateDbContext();
        await new ModuleQuantityGrantStore(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator())
            .GrantAsync(siteId, new ModuleKey(moduleKey), quantity, now, CancellationToken.None);
    }

    private async Task SeedModuleAsync(
        SiteId siteId, string moduleKey, bool grantedByOwner, DateTimeOffset enabledAt, DateTimeOffset? expiresAt,
        DateTimeOffset? revokedAt = null)
    {
        await using var db = fixture.CreateDbContext();
        db.EnabledModules.Add(new EnabledModule(
            new EnabledModuleId(Guid.NewGuid()),
            siteId,
            new ModuleKey(moduleKey),
            ["book-a-table"],
            new Uri("https://module.example.com/entry"),
            new ModuleCredential("a-sixteen-plus-character-secret"),
            enabledAt,
            grantedByOwner,
            expiresAt,
            revokedAt));
        await db.SaveChangesAsync();
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
        builder.Services.AddSingleton(fixture.DataSource);
        builder.Services.AddDbContext<AgoChatDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<Npgsql.NpgsqlDataSource>()));
        builder.Services.AddScoped<IOperatorRepository, OperatorRepository>();
        // `23-71`: ResolveOperatorIdentityHandler now composes IPermissionChecker - see
        // OfflineAutoReplyDeliveryEndToEndTests' own remarks on this same addition.
        builder.Services.AddScoped<IPermissionChecker, PermissionChecker>();
        builder.Services.AddScoped<ResolveOperatorIdentityHandler>();
        // The production registrations for this route - exactly as ChatModule/AddPostgresPersistence
        // make them, the same shape OwnerSitesEndpointTests' own host-builder uses.
        builder.Services.AddScoped<IPlatformOverviewReadStore, PlatformOverviewReadStore>();
        builder.Services.AddScoped<IEnabledModuleReadStore, EnabledModuleReadStore>();
        // `23-66`: GetSiteForOwnerHandler's own third read, alongside the two above - the module's
        // own granted quantity, kept apart from "not granted" (Quantity's own remarks on
        // OwnerSiteModuleDto).
        builder.Services.AddScoped<IModuleQuantityGrantStore, ModuleQuantityGrantStore>();
        builder.Services.AddScoped<IOutboxWriter, EfOutboxWriter<AgoChatDbContext>>();
        // `23-48`: GetSiteForOwnerHandler's own second dependency, added alongside the read stores
        // above - it loads the write-side aggregate directly for AllowedOrigins, the one field
        // IPlatformOverviewReadStore does not carry (that handler's own remarks).
        builder.Services.AddScoped<ISiteRepository, SiteRepository>();
        builder.Services.AddScoped<ListSitesForOwnerHandler>();
        builder.Services.AddScoped<GetSiteForOwnerHandler>();
        builder.Services.AddSingleton<IClock, Ago.Platform.Hosting.SystemClock>();
        // `24-12`: the owner endpoint's own access-record write - OwnerAccessRecorder resolves these
        // straight from DI, the same way the production host does.
        builder.Services.AddScoped<IAccessRecordRepository, AccessRecordRepository>();
        builder.Services.AddSingleton<IIdGenerator, UuidV7Generator>();
        builder.Services.AddHttpContextAccessor();
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

        // Only the detail route this file exercises - not MapOwnerEndpoints() (the list), which this
        // host has no registrations for. See OwnerSitesEndpoints' own class remarks for why the two
        // are separate Map calls.
        app.MapOwnerSiteDetailEndpoint();

        await app.StartAsync();
        return app;
    }
}
