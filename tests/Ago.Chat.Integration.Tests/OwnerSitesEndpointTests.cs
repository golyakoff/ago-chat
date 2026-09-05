using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Owner;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ListSitesForOwner;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Dapper;
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
/// `12-02`'s Done-when for the HTTP surface, against a real Keycloak and a real Postgres
/// (<see cref="OperatorOidcFixture"/>): who may call `GET /api/v1/owner/sites` at all, proven with
/// real tokens rather than read off `12-01`'s policy definition, and that the numbers the endpoint
/// serves are the tenant's real ones.
///
/// <para>Runs the production <c>OwnerSitesEndpoints.MapOwnerEndpoints</c>/
/// <c>ListSitesForOwnerHandler</c>/<c>PlatformOverviewReadStore</c> against a minimal
/// <see cref="TestServer"/>, the same <see cref="WebApplication"/> seam
/// <see cref="SiteRegistrationTests"/> established (this codebase's endpoint files are typed against
/// <see cref="WebApplication"/>, so building one is what lets a test call the real mapping instead of
/// re-declaring the route).</para>
///
/// <para><b>Why the exact-numbers sweep lives in
/// <see cref="PlatformOverviewReadStoreTests"/> and not here:</b> this fixture's database is shared
/// with every other class in its collection, several of which register sites of their own, so "the
/// response contains exactly these sites" is not a claim this file can make. What it can - and does -
/// assert is that a tenant it seeds itself, with its own varied numbers, comes back through the real
/// HTTP pipeline with every one of them correct.</para>
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class OwnerSitesEndpointTests(OperatorOidcFixture fixture)
{
    private const string Route = "/api/v1/owner/sites";

    [Fact]
    public async Task OwnerToken_GetsTheCrossTenantList()
    {
        var token = await fixture.GetPlatformOwnerAccessTokenAsync();

        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var response = await client.GetAsync($"{Route}?limit=200");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OwnerSitesResponse>();
        Assert.NotNull(body);
        // The cross-tenant claim itself: the owner sees a site they are not an operator of - in fact
        // the owner identity has no `operators` row anywhere (`12-01`).
        Assert.Contains(body.Sites, s => s.SiteId == fixture.SeededSiteId.Value);
        Assert.Equal(ListSitesForOwnerHandler.RecentWindowDays, body.RecentWindowDays);
        // `23-14`: present and consistent even on an unfiltered call - MatchingSites equals TotalSites
        // whenever no search was sent, and both are at least the one seeded site this test just proved
        // is present (this collection's database is shared, so ">=" is the honest assertion, not "=").
        Assert.Equal(body.MatchingSites, body.TotalSites);
        Assert.True(body.TotalSites >= 1);
    }

    /// <summary>`24-12`'s own Done-when for the owner surfaces: the cross-tenant list write its own
    /// access_records row through the real HTTP pipeline - `OwnerAccessRecorder` reads the caller's
    /// real Keycloak `sub` off the real token, not a stand-in.</summary>
    [Fact]
    public async Task OwnerToken_GetsTheCrossTenantList_AndLeavesAnAccessRecord_NamingTheRealOwnerSubject()
    {
        var token = await fixture.GetPlatformOwnerAccessTokenAsync();
        var ownerSubject = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
            .ReadJwtToken(token).Subject;

        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var before = await CountAccessRecordsAsync(AccessRecordKind.OwnerSiteList, ownerSubject);

        var response = await client.GetAsync($"{Route}?limit=200");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var after = await CountAccessRecordsAsync(AccessRecordKind.OwnerSiteList, ownerSubject);
        Assert.Equal(before + 1, after);

        // `AccessRecordKind.OwnerSiteList`'s own shape: this read spans every tenant, so the row it
        // wrote names no single site - proven over the real row, not merely asserted about the type.
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        var siteId = await connection.ExecuteScalarAsync<Guid?>(
            "select site_id from access_records where access_kind = 'OwnerSiteList' and actor_id = @actorId "
            + "order by id desc limit 1",
            new { actorId = ownerSubject });
        Assert.Null(siteId);
    }

    private async Task<long> CountAccessRecordsAsync(AccessRecordKind kind, string actorId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<long>(
            "select count(*) from access_records where access_kind = @kind and actor_id = @actorId",
            new { kind = kind.ToString(), actorId });
    }

    /// <summary>`23-14`'s own Done-when: searching by part of a site's name returns it, and the
    /// response states how many of how many - never a bare narrower page. Seeds a site with a
    /// deliberately distinctive name so the search is unambiguous against whatever else this shared
    /// collection's database holds.</summary>
    [Fact]
    public async Task OwnerToken_SearchingByPartOfAName_FindsTheSite_AndReportsMatchingOfTotal()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [], "Quixotic Quokka Supplies", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var token = await fixture.GetPlatformOwnerAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var unfiltered = await GetPageAsync(client, before: null, limit: 200);
        var searched = await client.GetAsync($"{Route}?query=Quixotic&limit=200");
        Assert.Equal(HttpStatusCode.OK, searched.StatusCode);
        var body = await searched.Content.ReadFromJsonAsync<OwnerSitesResponse>();
        Assert.NotNull(body);

        Assert.Contains(body.Sites, s => s.SiteId == siteId.Value);
        Assert.Equal(1L, body.MatchingSites);
        // The claim the item's own author asked to be guarded: the total never disappears or shrinks
        // to match the narrowed page - it is the same denominator the unfiltered call reports.
        Assert.Equal(unfiltered.TotalSites, body.TotalSites);
        Assert.True(body.MatchingSites < body.TotalSites, "The search must narrow the result for this test to prove anything.");
    }

    /// <summary>A search matching nothing: an empty `Sites` list, `MatchingSites` of zero, and
    /// `TotalSites` still the real count - never conflated with "the platform has no tenants".</summary>
    [Fact]
    public async Task OwnerToken_SearchingForANameThatMatchesNothing_ReturnsAnEmptyPage_ButTheRealTotal()
    {
        var token = await fixture.GetPlatformOwnerAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var unfiltered = await GetPageAsync(client, before: null, limit: 200);

        var response = await client.GetAsync($"{Route}?query=no-such-tenant-anywhere-in-this-database&limit=200");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OwnerSitesResponse>();
        Assert.NotNull(body);

        Assert.Empty(body.Sites);
        Assert.Equal(0L, body.MatchingSites);
        Assert.Equal(unfiltered.TotalSites, body.TotalSites);
    }

    /// <summary>The numbers, end to end over HTTP: a tenant seeded by this test with a deliberately
    /// distinctive shape - two seats, three conversations, one message inside the window and one
    /// well outside it, one live attachment and one deleted - read back through the real endpoint.
    /// </summary>
    [Fact]
    public async Task OwnerToken_SeesRealUsageNumbersForASeededTenant()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var createdAt = DateTimeOffset.UtcNow.AddDays(-3);
        await SeedTenantAsync(siteId, createdAt);

        var token = await fixture.GetPlatformOwnerAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var site = await FindSiteAsync(client, siteId.Value);

        Assert.Equal("Twelve Oh Two Tenant", site.Name);
        Assert.Equal(2L, site.SeatCount);
        Assert.Equal(3L, site.ConversationCount);
        Assert.Equal(1L, site.RecentMessageCount);
        Assert.Equal(4_242L, site.AttachmentBytes);
        Assert.NotNull(site.LastMessageAt);
        Assert.NotNull(site.CreatedAt);
        Assert.True((site.CreatedAt.Value - createdAt).Duration() < TimeSpan.FromMilliseconds(1));
        // `10-02`: exactly one tier exists, and this is it - not a computed or guessed value.
        Assert.Equal("free", site.Tier);
    }

    /// <summary>Keyset pagination over the live endpoint: two pages of one, and the second must
    /// continue where the first stopped.</summary>
    [Fact]
    public async Task OwnerToken_PagesWithBeforeAndLimit_WithoutGapOrDuplicate()
    {
        // Two sites of this test's own, so "there are at least two tenants to page over" is a fact it
        // establishes rather than one it inherits from whichever other class in this collection
        // happened to run first.
        await SeedBareSiteAsync();
        await SeedBareSiteAsync();

        var token = await fixture.GetPlatformOwnerAccessTokenAsync();
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var firstTwo = await GetPageAsync(client, before: null, limit: 2);
        Assert.Equal(2, firstTwo.Sites.Count);
        Assert.NotNull(firstTwo.NextBefore);

        var one = await GetPageAsync(client, before: null, limit: 1);
        var second = await GetPageAsync(client, before: one.NextBefore, limit: 1);

        Assert.Equal(firstTwo.Sites[0].SiteId, one.Sites[0].SiteId);
        Assert.Equal(firstTwo.Sites[1].SiteId, Assert.Single(second.Sites).SiteId);
        Assert.NotEqual(one.Sites[0].SiteId, second.Sites[0].SiteId);
    }

    /// <summary>`12-02`'s own Done-when: an ordinary operator, with a real Keycloak-signed token and
    /// a real `operators` row, is refused.</summary>
    [Fact]
    public async Task OrdinaryOperatorToken_IsRejected()
    {
        var token = await fixture.GetDemoOperatorAccessTokenAsync();

        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Route)).StatusCode);
    }

    /// <summary>The case that matters most: `5-08`'s site-wide `"Admin"`, holding `site:configure`
    /// for their own site, is still not the platform owner. A permission granted broadly inside one
    /// tenant does not become cross-tenant read access - which is the entire reason `adr/0032` put
    /// the owner outside the RBAC tables rather than adding a very powerful role to them.</summary>
    [Fact]
    public async Task SiteConfigureHoldingAdminToken_IsRejected()
    {
        await using (var db = fixture.CreateDbContext())
        {
            // Proven, not assumed: this operator really does hold the permission being refused here.
            var checker = new PermissionChecker(db);
            Assert.True(await checker.HasPermissionAsync(
                fixture.SeededAdminOperatorId, fixture.SeededSiteId, Permission.SiteConfigure, CancellationToken.None));
        }

        var token = await fixture.GetDemoAdminAccessTokenAsync();

        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Route)).StatusCode);
    }

    [Fact]
    public async Task NoToken_IsRejected()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Route)).StatusCode);
    }

    private static async Task<OwnerSitesResponse> GetPageAsync(HttpClient client, Guid? before, int limit)
    {
        var query = before is { } cursor ? $"?before={cursor}&limit={limit}" : $"?limit={limit}";
        var response = await client.GetAsync($"{Route}{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OwnerSitesResponse>();
        Assert.NotNull(body);
        return body;
    }

    /// <summary>Walks the keyset pages until the wanted site turns up - the collection's database is
    /// shared, so the number of sites in it is not something this file may assume.</summary>
    private static async Task<OwnerSiteSummaryDto> FindSiteAsync(HttpClient client, Guid siteId)
    {
        Guid? cursor = null;
        do
        {
            var page = await GetPageAsync(client, cursor, limit: 50);
            var match = page.Sites.FirstOrDefault(s => s.SiteId == siteId);
            if (match is not null)
            {
                return match;
            }

            cursor = page.NextBefore;
        } while (cursor is not null);

        Assert.Fail($"Site {siteId} was not present on any page of the owner overview.");
        throw new InvalidOperationException("unreachable");
    }

    private async Task SeedBareSiteAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [], "Pagination Filler", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
    }

    private async Task SeedTenantAsync(SiteId siteId, DateTimeOffset createdAt)
    {
        var now = DateTimeOffset.UtcNow;
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationIds = Enumerable.Range(0, 3).Select(_ => new ConversationId(Guid.NewGuid())).ToList();

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [], "Twelve Oh Two Tenant", createdAt));
            db.Operators.Add(new Operator(new OperatorId(Guid.NewGuid()), siteId, OperatorStatus.Offline, capacity: 5));
            db.Operators.Add(new Operator(new OperatorId(Guid.NewGuid()), siteId, OperatorStatus.Offline, capacity: 5));
            db.Visitors.Add(new Visitor(visitorId, siteId, now));
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            foreach (var conversationId in conversationIds)
            {
                db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, now));
            }

            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            var live = Attachment.CreatePending(
                new AttachmentId(Guid.NewGuid()), siteId, conversationIds[0], "seed/live", "image/png", 4_242, now);
            live.ConfirmReady(4_242, "image/png", now);
            db.Attachments.Add(live);

            var deleted = Attachment.CreatePending(
                new AttachmentId(Guid.NewGuid()), siteId, conversationIds[0], "seed/gone", "image/png", 9_000, now);
            deleted.ConfirmReady(9_000, "image/png", now);
            deleted.MarkDeleted();
            db.Attachments.Add(deleted);

            await db.SaveChangesAsync();
        }

        // One message inside the recent window and one far outside it, so the endpoint's count has
        // something to exclude. The older one needs its own monthly partition to exist first
        // (`2-06`): nothing creates past partitions, since production never writes into one.
        var old = now.AddDays(-120);
        // `15-09`/`adr/0087`: no partition to create ahead of the insert any more - `messages` is
        // `PARTITION BY HASH (site_id)`, 64 fixed buckets that already exist for every site, so a
        // message dated 120 days in the past inserts exactly as easily as one dated yesterday. This is
        // the structural fix this file's own comment above used to work around by creating a partition
        // first; `MessagePartitioningTests` has the dedicated proof.
        await InsertMessageAsync(siteId, conversationIds[0], visitorId, sequence: 1, createdAt: now.AddDays(-1));
        await InsertMessageAsync(siteId, conversationIds[0], visitorId, sequence: 2, createdAt: old);
    }

    private async Task InsertMessageAsync(
        SiteId siteId, ConversationId conversationId, VisitorId visitorId, int sequence, DateTimeOffset createdAt)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new Npgsql.NpgsqlCommand("""
            insert into messages (id, conversation_id, sequence, author_kind, author_id, body, created_at, retention_class, site_id)
            values (@id, @conversationId, @sequence, 'Visitor', @authorId, 'seeded', @createdAt, 'free', @siteId)
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("conversationId", conversationId.Value);
        command.Parameters.AddWithValue("sequence", sequence);
        command.Parameters.AddWithValue("authorId", visitorId.Value);
        command.Parameters.AddWithValue("createdAt", createdAt);
        command.Parameters.AddWithValue("siteId", siteId.Value);
        await command.ExecuteNonQueryAsync();
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
        builder.Services.AddScoped<ResolveOperatorIdentityHandler>();
        // The production registrations for this route, exactly as ChatModule/AddPostgresPersistence
        // make them.
        builder.Services.AddScoped<IPlatformOverviewReadStore, PlatformOverviewReadStore>();
        builder.Services.AddScoped<ListSitesForOwnerHandler>();
        builder.Services.AddSingleton<IClock, Ago.Platform.Hosting.SystemClock>();
        // `24-12`: the owner endpoint's own access-record write - OwnerAccessRecorder resolves these
        // three straight from DI, the same way the production host does.
        builder.Services.AddScoped<IAccessRecordRepository, AccessRecordRepository>();
        builder.Services.AddSingleton<IIdGenerator, UuidV7Generator>();
        // Registered exactly as the real host registers it - the owner token must be accepted while
        // this transformation runs and resolves nothing, since a platform owner has no `operators`
        // row (`12-01`).
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
            // `Program.cs`'s own declaration, reproduced verbatim.
            options.AddPolicy("RequirePlatformOwner", policy => policy
                .AddAuthenticationSchemes(JwtSchemes.Operator)
                .RequireAuthenticatedUser()
                .AddRequirements(new PlatformOwnerRequirement()));
        });
        builder.Services.AddSingleton<IAuthorizationHandler, PlatformOwnerAuthorizationHandler>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        // The real production mapping - no duplicated route or policy decision.
        app.MapOwnerEndpoints();

        await app.StartAsync();
        return app;
    }
}
