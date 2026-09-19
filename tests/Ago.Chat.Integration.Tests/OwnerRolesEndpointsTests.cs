using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Owner;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Application.UseCases.AddRolePermissionsAsOwner;
using Ago.Chat.Application.UseCases.RemoveRolePermissionsAsOwner;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
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
/// `25-76`'s own Done-when, over a real HTTP pipeline, a real Postgres and real Keycloak-signed tokens
/// (<see cref="OperatorOidcFixture"/>) - the same infrastructure and the identical test-host shape
/// <see cref="OwnerOperatorsEndpointsTests"/> already establishes for its own owner-only recovery
/// route.
///
/// <para><b>Every mutating test seeds its own fresh site</b>, never <c>fixture.SeededSiteId</c> -
/// <see cref="OperatorOidcFixture"/> is a shared, collection-wide fixture
/// (<c>OwnerOperatorsEndpointsTests</c>' own remarks: "shared, real Postgres state across every test
/// in this collection"), so a test that actually widens a role's permissions on the shared site would
/// leak into every other test in the collection that assumes a clean one - found live, the hard way,
/// by this file's own first draft: a test asserting `channel:manage` was *not yet* present failed
/// because an earlier test in the same run had already added it to the identical shared row. Only the
/// non-mutating tests below (a role name that does not resolve at all, and the authorization-boundary
/// checks, all refused before the handler ever touches a row) still use the shared fixture, the same
/// restraint <c>OwnerOperatorsEndpointsTests.OwnerToken_ForANonexistentOperator_Returns404</c> already
/// shows for its own non-mutating case.</para>
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class OwnerRolesEndpointsTests(OperatorOidcFixture fixture)
{
    /// <summary>The exact live-deployment gap this item was found from, restated as a fixture default
    /// rather than described - `RegisterSiteHandler.AdminRolePermissions`' own founder list, minus
    /// `channel:manage`.</summary>
    private static readonly List<string> BaselineAdminPermissions =
        [Permission.SiteConfigure.Value, Permission.SiteManageOperators.Value, Permission.AttachmentDelete.Value];

    private static string PermissionsRoute(SiteId siteId, string roleName) =>
        $"/api/v1/owner/sites/{siteId.Value}/roles/{roleName}/permissions";

    /// <summary>The item's own headline Done-when, end to end: the real live gap - `channel:manage`
    /// missing from a site's `Admin` role - closed through the real route, and the real row (not
    /// merely the response) now carries it.</summary>
    [Fact]
    public async Task OwnerToken_AddingAMissingPermission_Succeeds_AndTheRealRowReflectsIt()
    {
        var siteId = await SeedSiteWithAdminRoleAsync();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await ownerClient.PutAsJsonAsync(
            PermissionsRoute(siteId, "Admin"),
            new OwnerRolesEndpoints.AddRolePermissionsRequest([Permission.ChannelManage.Value]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OwnerRolesEndpoints.AddRolePermissionsResponse>();
        Assert.NotNull(body);
        Assert.Equal("Admin", body.RoleName);

        var role = await GetRoleAsync(siteId, "Admin");
        Assert.NotNull(role);
        Assert.Contains(Permission.ChannelManage.Value, role.Permissions);
        // The permissions this role already carried are untouched - this is an add, never a replace.
        Assert.Contains(Permission.SiteConfigure.Value, role.Permissions);
        Assert.Contains(Permission.SiteManageOperators.Value, role.Permissions);
        Assert.Contains(Permission.AttachmentDelete.Value, role.Permissions);
    }

    /// <summary>The propagation half of the same Done-when: an operator already holding `Admin` on
    /// this fresh site, with a linked external identity, learns the new permission without signing
    /// out and back in - proven by reading the real outbox row, the same `RoleAssignmentsChanged` fact
    /// `AddPermissionsAsync`'s own doc comment promises.</summary>
    [Fact]
    public async Task OwnerToken_AddingAMissingPermission_StagesARoleAssignmentsChangedRow_ForTheOperatorAlreadySignedIn()
    {
        var (siteId, externalSubjectId) = await SeedSiteWithAdminRoleAndLinkedOperatorAsync();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await ownerClient.PutAsJsonAsync(
            PermissionsRoute(siteId, "Admin"),
            new OwnerRolesEndpoints.AddRolePermissionsRequest([Permission.ChannelManage.Value]));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var db = fixture.CreateDbContext();
        var outboxRow = await db.Set<OutboxMessage>()
            .Where(o => o.Type == nameof(RoleAssignmentsChanged) && o.PartitionKey == externalSubjectId)
            .OrderByDescending(o => o.Id)
            .FirstOrDefaultAsync(CancellationToken.None);
        Assert.NotNull(outboxRow);
        var contract = JsonSerializer.Deserialize<RoleAssignmentsChanged>(outboxRow.Payload)!;
        Assert.Equal(siteId.Value, contract.SiteId);
        Assert.Contains(Permission.ChannelManage.Value, contract.Permissions);
    }

    /// <summary>Fails-before, over the real route: a permission string that is not a real, known
    /// <see cref="Permission"/> is refused with a `400`, and the real row is left exactly as it was.
    /// </summary>
    [Fact]
    public async Task OwnerToken_WithAnUnknownPermission_IsRefused_AndTheRealRowIsUnchanged()
    {
        var siteId = await SeedSiteWithAdminRoleAsync();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await ownerClient.PutAsJsonAsync(
            PermissionsRoute(siteId, "Admin"),
            new OwnerRolesEndpoints.AddRolePermissionsRequest(["not:a-real-permission"]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var role = await GetRoleAsync(siteId, "Admin");
        Assert.NotNull(role);
        Assert.DoesNotContain("not:a-real-permission", role.Permissions);
        Assert.DoesNotContain(Permission.ChannelManage.Value, role.Permissions);
        Assert.Equal(BaselineAdminPermissions.OrderBy(p => p, StringComparer.Ordinal), role.Permissions.OrderBy(p => p, StringComparer.Ordinal));
    }

    /// <summary>Not a mutating test - "SuperAdmin" resolves to no role on any site, so this never
    /// reaches a row to touch, and the shared fixture's own site is safe to name here.</summary>
    [Fact]
    public async Task OwnerToken_ForARoleNameThatDoesNotExistOnThisSite_Returns400()
    {
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await ownerClient.PutAsJsonAsync(
            PermissionsRoute(fixture.SeededSiteId, "SuperAdmin"),
            new OwnerRolesEndpoints.AddRolePermissionsRequest([Permission.ChannelManage.Value]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>`24-12`: the access record, proven with the real owner subject off the real token and
    /// the role's own real row id - the identical proof `OwnerOperatorsEndpointsTests`' own equivalent
    /// test gives for the seat-restore route.</summary>
    [Fact]
    public async Task OwnerToken_AddingAPermission_LeavesAnAccessRecord_NamingTheRealOwnerSubject_AndTheRole()
    {
        var siteId = await SeedSiteWithAdminRoleAsync();
        var token = await fixture.GetPlatformOwnerAccessTokenAsync();
        var ownerSubject = new JwtSecurityTokenHandler().ReadJwtToken(token).Subject;
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, token);

        var response = await ownerClient.PutAsJsonAsync(
            PermissionsRoute(siteId, "Admin"),
            new OwnerRolesEndpoints.AddRolePermissionsRequest([Permission.ChannelManage.Value]));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var role = await GetRoleAsync(siteId, "Admin");
        Assert.NotNull(role);

        var records = await new AccessRecordRepository(fixture.DataSource)
            .ListForSiteAsync(siteId, beforeId: null, limit: 50, CancellationToken.None);
        var recorded = Assert.Single(
            records.Items, r => r.AccessKind == AccessRecordKind.OwnerRolePermissionsGrant && r.ResourceId == role.Id);
        Assert.Equal(AccessRecordActorKind.PlatformOwner, recorded.ActorKind);
        Assert.Equal(ownerSubject, recorded.ActorId);
        Assert.Equal(AccessRecordResourceKind.Role, recorded.ResourceKind);
    }

    // ------------------------------------------------------------------------------------------
    // `25-77`: the removal direction - DELETE on the identical resource the PUT tests above already
    // name. Every mutating test here seeds its own fresh site too, the same restraint this file's own
    // class remarks state for the add direction.
    // ------------------------------------------------------------------------------------------

    private static HttpRequestMessage DeleteRequest(SiteId siteId, string roleName, OwnerRolesEndpoints.RemoveRolePermissionsRequest body) =>
        new(HttpMethod.Delete, PermissionsRoute(siteId, roleName)) { Content = System.Net.Http.Json.JsonContent.Create(body) };

    /// <summary>The item's own headline Done-when, end to end: "no magic roles" - one of `Admin`'s own
    /// defining permissions is removed through the real route, and the real row (not merely the
    /// response) reflects it.</summary>
    [Fact]
    public async Task OwnerToken_RemovingAnAdminDefiningPermission_Succeeds_AndTheRealRowReflectsIt_NoCarveOut()
    {
        var siteId = await SeedSiteWithAdminRoleAsync();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await ownerClient.SendAsync(DeleteRequest(
            siteId, "Admin",
            new OwnerRolesEndpoints.RemoveRolePermissionsRequest(
                [Permission.SiteManageOperators.Value], "the tenant asked for a narrower Admin role")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OwnerRolesEndpoints.RemoveRolePermissionsResponse>();
        Assert.NotNull(body);
        Assert.Equal("Admin", body.RoleName);

        var role = await GetRoleAsync(siteId, "Admin");
        Assert.NotNull(role);
        Assert.DoesNotContain(Permission.SiteManageOperators.Value, role.Permissions);
        Assert.Contains(Permission.SiteConfigure.Value, role.Permissions);
        Assert.Contains(Permission.AttachmentDelete.Value, role.Permissions);
    }

    /// <summary>The propagation half of the same Done-when: an operator already holding `Admin` on
    /// this fresh site, with a linked external identity, learns the narrower permission set without
    /// signing out and back in.</summary>
    [Fact]
    public async Task OwnerToken_RemovingAPermission_StagesARoleAssignmentsChangedRow_ForTheOperatorAlreadySignedIn()
    {
        var (siteId, externalSubjectId) = await SeedSiteWithAdminRoleAndLinkedOperatorAsync();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await ownerClient.SendAsync(DeleteRequest(
            siteId, "Admin",
            new OwnerRolesEndpoints.RemoveRolePermissionsRequest([Permission.SiteConfigure.Value], "narrowing this role")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var db = fixture.CreateDbContext();
        var outboxRow = await db.Set<OutboxMessage>()
            .Where(o => o.Type == nameof(RoleAssignmentsChanged) && o.PartitionKey == externalSubjectId)
            .OrderByDescending(o => o.Id)
            .FirstOrDefaultAsync(CancellationToken.None);
        Assert.NotNull(outboxRow);
        var contract = JsonSerializer.Deserialize<RoleAssignmentsChanged>(outboxRow.Payload)!;
        Assert.Equal(siteId.Value, contract.SiteId);
        Assert.DoesNotContain(Permission.SiteConfigure.Value, contract.Permissions);
    }

    /// <summary>Fails-before, over the real route: a blank reason is refused with `400`, and the real
    /// row is left exactly as it was.</summary>
    [Fact]
    public async Task OwnerToken_RemovingWithNoReason_IsRefused_AndTheRealRowIsUnchanged()
    {
        var siteId = await SeedSiteWithAdminRoleAsync();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await ownerClient.SendAsync(DeleteRequest(
            siteId, "Admin", new OwnerRolesEndpoints.RemoveRolePermissionsRequest([Permission.SiteConfigure.Value], "")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var role = await GetRoleAsync(siteId, "Admin");
        Assert.NotNull(role);
        Assert.Contains(Permission.SiteConfigure.Value, role.Permissions);
    }

    /// <summary>Fails-before, over the real route: an unknown permission is refused with `400`, and the
    /// real row is left exactly as it was.</summary>
    [Fact]
    public async Task OwnerToken_RemovingAnUnknownPermission_IsRefused_AndTheRealRowIsUnchanged()
    {
        var siteId = await SeedSiteWithAdminRoleAsync();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await ownerClient.SendAsync(DeleteRequest(
            siteId, "Admin", new OwnerRolesEndpoints.RemoveRolePermissionsRequest(["not:a-real-permission"], "a real reason")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var role = await GetRoleAsync(siteId, "Admin");
        Assert.NotNull(role);
        Assert.Equal(BaselineAdminPermissions.OrderBy(p => p, StringComparer.Ordinal), role.Permissions.OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public async Task OwnerToken_RemovingFromARoleNameThatDoesNotExist_Returns400()
    {
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await ownerClient.SendAsync(DeleteRequest(
            fixture.SeededSiteId, "SuperAdmin",
            new OwnerRolesEndpoints.RemoveRolePermissionsRequest([Permission.ChannelManage.Value], "a real reason")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>`24-12`: the access record, proven with the real owner subject off the real token and
    /// the role's own real row id - `OwnerRolePermissionsRemoval`, a distinct kind from the grant
    /// direction's own `OwnerRolePermissionsGrant` proven a few tests up.</summary>
    [Fact]
    public async Task OwnerToken_RemovingAPermission_LeavesAnAccessRecord_NamingTheRealOwnerSubject_AndTheRole()
    {
        var siteId = await SeedSiteWithAdminRoleAsync();
        var token = await fixture.GetPlatformOwnerAccessTokenAsync();
        var ownerSubject = new JwtSecurityTokenHandler().ReadJwtToken(token).Subject;
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, token);

        var response = await ownerClient.SendAsync(DeleteRequest(
            siteId, "Admin",
            new OwnerRolesEndpoints.RemoveRolePermissionsRequest([Permission.SiteManageOperators.Value], "removed after review")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var role = await GetRoleAsync(siteId, "Admin");
        Assert.NotNull(role);

        var records = await new AccessRecordRepository(fixture.DataSource)
            .ListForSiteAsync(siteId, beforeId: null, limit: 50, CancellationToken.None);
        var recorded = Assert.Single(
            records.Items, r => r.AccessKind == AccessRecordKind.OwnerRolePermissionsRemoval && r.ResourceId == role.Id);
        Assert.Equal(AccessRecordActorKind.PlatformOwner, recorded.ActorKind);
        Assert.Equal(ownerSubject, recorded.ActorId);
        Assert.Equal(AccessRecordResourceKind.Role, recorded.ResourceKind);
    }

    /// <summary>The `role_permission_removal_overrides` row itself, proven with the real reason and the
    /// real owner subject - the `24-12` access record proves "an owner removed something from this
    /// role"; this table proves "why", the same split `module_revoke_overrides` draws against its own
    /// access-record sibling.</summary>
    [Fact]
    public async Task OwnerToken_RemovingAPermission_RecordsTheReason_InTheOverrideTable()
    {
        var siteId = await SeedSiteWithAdminRoleAsync();
        var token = await fixture.GetPlatformOwnerAccessTokenAsync();
        var ownerSubject = new JwtSecurityTokenHandler().ReadJwtToken(token).Subject;
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, token);

        const string reason = "the tenant's own support ticket #4821 asked for this permission removed";
        var response = await ownerClient.SendAsync(DeleteRequest(
            siteId, "Admin", new OwnerRolesEndpoints.RemoveRolePermissionsRequest([Permission.AttachmentDelete.Value], reason)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var db = fixture.CreateDbContext();
        var recorded = await db.Set<RolePermissionRemovalOverrideEntity>()
            .AsNoTracking().SingleAsync(o => o.SiteId == siteId, CancellationToken.None);
        Assert.Equal("Admin", recorded.RoleName);
        Assert.Equal([Permission.AttachmentDelete.Value], recorded.Permissions);
        Assert.Equal(ownerSubject, recorded.RemovedBy);
        Assert.Equal(reason, recorded.Reason);
    }

    // ------------------------------------------------------------------------------------------
    // The authorization boundary: this route is RequirePlatformOwner and nothing weaker - the
    // identical assertions OwnerOperatorsEndpointsTests' own equivalent tests already make for their
    // own owner-only route.
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task OrdinaryOperatorToken_IsRejected()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, await fixture.GetDemoOperatorAccessTokenAsync());

        var response = await client.PutAsJsonAsync(
            PermissionsRoute(fixture.SeededSiteId, "Admin"), new OwnerRolesEndpoints.AddRolePermissionsRequest([Permission.ChannelManage.Value]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>`25-77`'s own removal-side boundary check - the identical lesson, the identical route,
    /// the opposite HTTP verb.</summary>
    [Fact]
    public async Task OrdinaryOperatorToken_IsRejected_OnTheRemovalRouteToo()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, await fixture.GetDemoOperatorAccessTokenAsync());

        var response = await client.SendAsync(DeleteRequest(
            fixture.SeededSiteId, "Admin",
            new OwnerRolesEndpoints.RemoveRolePermissionsRequest([Permission.SiteManageOperators.Value], "a real reason")));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>The case that matters most, the identical lesson `OwnerOperatorsEndpointsTests`'s own
    /// equivalent test already proves: `5-08`'s site-wide `"Admin"`, holding `site:manage_operators`
    /// for their own site, is still not the platform owner - and, pointedly, cannot widen their own
    /// role's permissions through this route either.</summary>
    [Fact]
    public async Task SiteManageOperatorsHoldingAdminToken_IsRejected()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, await fixture.GetDemoAdminAccessTokenAsync());

        var response = await client.PutAsJsonAsync(
            PermissionsRoute(fixture.SeededSiteId, "Admin"), new OwnerRolesEndpoints.AddRolePermissionsRequest([Permission.ChannelManage.Value]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NoToken_IsRejected()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token: null);

        var response = await client.PutAsJsonAsync(
            PermissionsRoute(fixture.SeededSiteId, "Admin"), new OwnerRolesEndpoints.AddRolePermissionsRequest([Permission.ChannelManage.Value]));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Reads the real row back through a fresh <see cref="RoleRepository"/> instance and a
    /// fresh <see cref="AgoChatDbContext"/> - never the same context the endpoint's own request used,
    /// the identical "prove the write actually committed" discipline
    /// <see cref="OwnerOperatorsEndpointsTests.GetOperatorAsync"/> follows for its own sibling route.
    /// </summary>
    private async Task<RoleLookup?> GetRoleAsync(SiteId siteId, string roleName)
    {
        await using var db = fixture.CreateDbContext();
        return await new RoleRepository(db, new UuidV7Generator(), new Ago.Platform.Hosting.SystemClock()).GetByNameAsync(
            siteId, roleName, CancellationToken.None);
    }

    /// <summary>A fresh site with an `Admin` role carrying <see cref="BaselineAdminPermissions"/> - no
    /// operator holds it, for the tests above that only care about the row itself. Every mutating test
    /// gets its own site rather than reusing <c>fixture.SeededSiteId</c> - see this class's own remarks
    /// for why.</summary>
    private async Task<SiteId> SeedSiteWithAdminRoleAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Roles.Add(new RoleRecord
        {
            Id = Guid.NewGuid(),
            SiteId = siteId,
            Name = "Admin",
            Permissions = [.. BaselineAdminPermissions],
        });
        await db.SaveChangesAsync();
        return siteId;
    }

    /// <summary>The same fresh site as <see cref="SeedSiteWithAdminRoleAsync"/>, plus one operator
    /// holding the `Admin` role with a linked external identity - what the propagation test needs to
    /// prove a currently-signed-in operator learns a newly added permission, the identical seeding
    /// shape `RoleRepositoryTests.AddPermissionsAsync_ForASingleHolder_...`'s own remarks establish for
    /// `AddPermissionsAsync` itself.</summary>
    private async Task<(SiteId SiteId, string ExternalSubjectId)> SeedSiteWithAdminRoleAndLinkedOperatorAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var externalSubjectId = $"sub-{Guid.NewGuid():N}";
        var roleId = Guid.NewGuid();
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5, externalSubjectId));
        db.Roles.Add(new RoleRecord
        {
            Id = roleId,
            SiteId = siteId,
            Name = "Admin",
            Permissions = [.. BaselineAdminPermissions],
        });
        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
        await db.SaveChangesAsync();
        return (siteId, externalSubjectId);
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
        // `25-170`: ResolveOperatorIdentityHandler now composes IOperatorRoleRepository instead of
        // IPermissionChecker - CanSignIn is the one-rule "does any held role still hold its own seat" form.
        builder.Services.AddScoped<IOperatorRoleRepository, OperatorRoleRepository>();
        builder.Services.AddScoped<ResolveOperatorIdentityHandler>();
        builder.Services.AddScoped<IPermissionChecker, PermissionChecker>();
        builder.Services.AddScoped<ISiteRepository, SiteRepository>();
        // A real repository, not a fake - this suite already runs against a real Postgres
        // (fixture.DataSource), and the whole point of this file is proving a real row - both `roles`
        // and the outbox - lands.
        builder.Services.AddScoped<IRoleRepository, RoleRepository>();
        builder.Services.AddScoped<AddRolePermissionsAsOwnerHandler>();
        // `25-77`: the removal mirror - same real repository, same reason this whole file gives.
        builder.Services.AddScoped<RemoveRolePermissionsAsOwnerHandler>();
        // `24-12`: the owner endpoint's own access-record write - OwnerAccessRecorder resolves this
        // straight from DI, the same way the production host does. IClock/IIdGenerator are already
        // registered above (AddPlatformKernel).
        builder.Services.AddScoped<IAccessRecordRepository, AccessRecordRepository>();
        builder.Services.AddSingleton<IClock, Ago.Platform.Hosting.SystemClock>();

        builder.Services.AddHttpContextAccessor();
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

        app.MapOwnerRolesEndpoints();

        await app.StartAsync();
        return app;
    }
}
