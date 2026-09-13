using System.Text.Json;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Application.UseCases.RemoveRolePermissionsAsOwner;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-77`: the platform owner's own removal write, run against a real Postgres rather than
/// <c>Ago.Chat.Application.Tests</c>' own faked <c>IRoleRepository</c> - the same reason
/// <see cref="RoleRepositoryTests"/> exists at all for <c>RemovePermissionsAsync</c> itself
/// (that file's own remarks: the fakes have no outbox and no override table to write into).
///
/// <para><b>This file's own headline proof</b>, the removal-side mirror of
/// <c>AddRolePermissionsAsOwnerHandlerTests</c>'s own: the *owner's own write path* -
/// <see cref="RemoveRolePermissionsAsOwnerHandler"/> - actually reaches
/// <c>IRoleRepository.RemovePermissionsAsync</c> with the right arguments end to end, not merely
/// against a fake. Every test here seeds its own fresh <see cref="SiteId"/> (never a shared fixture's
/// seeded site) - the exact shared-fixture-pollution trap `25-76`'s own
/// <c>OwnerRolesEndpointsTests</c> found the hard way, deliberately not repeated here.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RemoveRolePermissionsAsOwnerHandlerTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now =
        new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    /// <summary>The two real gaps `25-76` was found from are `channel:manage`/`conversation:close`
    /// missing; this item's own mirror is "the owner can take one away, real row reflects it, real
    /// operator learns it" - both proven here in one test.</summary>
    [Fact]
    public async Task HandleAsync_RemovingAPermission_WritesTheRole_AndReachesAnAlreadySignedInOperator()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var externalSubjectId = $"sub-{Guid.NewGuid():N}";
        var roleId = Guid.NewGuid();

        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            seed.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId));
            seed.Roles.Add(new RoleRecord
            {
                Id = roleId,
                SiteId = siteId,
                Name = "Admin",
                Permissions = [Permission.SiteConfigure.Value, Permission.SiteManageOperators.Value, Permission.AttachmentDelete.Value],
            });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var roleRepository = new RoleRepository(db, new UuidV7Generator(), new FixedClock(Now));
            var handler = new RemoveRolePermissionsAsOwnerHandler(roleRepository);

            var result = await handler.HandleAsync(
                new RemoveRolePermissionsAsOwner(
                    siteId, "Admin", [Permission.SiteManageOperators.Value], "owner-sub",
                    "no longer needed on this tenant's own Admin role"),
                CancellationToken.None);

            Assert.True(result.IsSuccess);
        }

        await using var verify = fixture.CreateDbContext();
        var role = await verify.Set<RoleRecord>().AsNoTracking().SingleAsync(r => r.Id == roleId, CancellationToken.None);
        Assert.DoesNotContain(Permission.SiteManageOperators.Value, role.Permissions);
        Assert.Contains(Permission.SiteConfigure.Value, role.Permissions);
        Assert.Contains(Permission.AttachmentDelete.Value, role.Permissions);

        var outboxRow = await verify.Set<OutboxMessage>().SingleAsync(
            o => o.Type == nameof(RoleAssignmentsChanged) && o.PartitionKey == externalSubjectId, CancellationToken.None);
        var contract = JsonSerializer.Deserialize<RoleAssignmentsChanged>(outboxRow.Payload)!;
        Assert.Equal(siteId.Value, contract.SiteId);
        Assert.DoesNotContain(Permission.SiteManageOperators.Value, contract.Permissions);
        Assert.Null(outboxRow.PublishedAt);

        var recorded = await verify.Set<RolePermissionRemovalOverrideEntity>()
            .AsNoTracking().SingleAsync(o => o.SiteId == siteId, CancellationToken.None);
        Assert.Equal("owner-sub", recorded.RemovedBy);
        Assert.Equal("no longer needed on this tenant's own Admin role", recorded.Reason);
    }

    /// <summary>"No magic roles", proven end to end through the real HTTP-adjacent handler rather than
    /// only at the fake-backed unit level: removing `Admin`'s own other defining permission,
    /// `site:configure`, is not refused either.</summary>
    [Fact]
    public async Task HandleAsync_RemovingTheOtherAdminDefiningPermission_AlsoSucceeds_NoCarveOut()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var roleId = Guid.NewGuid();
        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            seed.Roles.Add(new RoleRecord
            {
                Id = roleId,
                SiteId = siteId,
                Name = "Admin",
                Permissions = [Permission.SiteConfigure.Value, Permission.SiteManageOperators.Value],
            });
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var roleRepository = new RoleRepository(db, new UuidV7Generator(), new FixedClock(Now));
            var handler = new RemoveRolePermissionsAsOwnerHandler(roleRepository);

            var result = await handler.HandleAsync(
                new RemoveRolePermissionsAsOwner(
                    siteId, "Admin", [Permission.SiteConfigure.Value], "owner-sub", "the owner's own call"),
                CancellationToken.None);

            Assert.True(result.IsSuccess);
        }

        await using var verify = fixture.CreateDbContext();
        var role = await verify.Set<RoleRecord>().AsNoTracking().SingleAsync(r => r.Id == roleId, CancellationToken.None);
        Assert.Equal([Permission.SiteManageOperators.Value], role.Permissions);
    }

    /// <summary>Fails-before, against a real database: a blank reason is refused before
    /// <c>IRoleRepository.RemovePermissionsAsync</c> is ever called - the real row, the real outbox and
    /// the real override table are all left exactly as they were.</summary>
    [Fact]
    public async Task HandleAsync_WithNoReason_WritesNothing_ToTheRoleOutboxOrOverrideTable()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var externalSubjectId = $"sub-{Guid.NewGuid():N}";
        var roleId = Guid.NewGuid();
        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            seed.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId));
            seed.Roles.Add(new RoleRecord { Id = roleId, SiteId = siteId, Name = "Admin", Permissions = [Permission.SiteConfigure.Value] });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var roleRepository = new RoleRepository(db, new UuidV7Generator(), new FixedClock(Now));
            var handler = new RemoveRolePermissionsAsOwnerHandler(roleRepository);

            var result = await handler.HandleAsync(
                new RemoveRolePermissionsAsOwner(siteId, "Admin", [Permission.SiteConfigure.Value], "owner-sub", ""),
                CancellationToken.None);

            Assert.True(result.IsFailure);
            Assert.Equal("Role.PermissionRemovalReasonRequired", result.Error!.Value.Code);
        }

        await using var verify = fixture.CreateDbContext();
        var role = await verify.Set<RoleRecord>().AsNoTracking().SingleAsync(r => r.Id == roleId, CancellationToken.None);
        Assert.Equal([Permission.SiteConfigure.Value], role.Permissions);
        Assert.False(await verify.Set<OutboxMessage>()
            .AnyAsync(o => o.Type == nameof(RoleAssignmentsChanged) && o.PartitionKey == externalSubjectId, CancellationToken.None));
        Assert.False(await verify.Set<RolePermissionRemovalOverrideEntity>()
            .AnyAsync(o => o.SiteId == siteId, CancellationToken.None));
    }

    /// <summary>Fails-before, against a real database: an unknown permission never reaches `roles`, the
    /// outbox, or the override table - refused entirely in the handler.</summary>
    [Fact]
    public async Task HandleAsync_WithAnUnknownPermission_WritesNothing_ToTheRoleOutboxOrOverrideTable()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var externalSubjectId = $"sub-{Guid.NewGuid():N}";
        var roleId = Guid.NewGuid();
        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            seed.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId));
            seed.Roles.Add(new RoleRecord { Id = roleId, SiteId = siteId, Name = "Admin", Permissions = [Permission.SiteConfigure.Value] });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var roleRepository = new RoleRepository(db, new UuidV7Generator(), new FixedClock(Now));
            var handler = new RemoveRolePermissionsAsOwnerHandler(roleRepository);

            var result = await handler.HandleAsync(
                new RemoveRolePermissionsAsOwner(siteId, "Admin", ["not:a-real-permission"], "owner-sub", "a real reason"),
                CancellationToken.None);

            Assert.True(result.IsFailure);
            Assert.Equal("Role.PermissionUnknown", result.Error!.Value.Code);
        }

        await using var verify = fixture.CreateDbContext();
        var role = await verify.Set<RoleRecord>().AsNoTracking().SingleAsync(r => r.Id == roleId, CancellationToken.None);
        Assert.Equal([Permission.SiteConfigure.Value], role.Permissions);
        Assert.False(await verify.Set<OutboxMessage>()
            .AnyAsync(o => o.Type == nameof(RoleAssignmentsChanged) && o.PartitionKey == externalSubjectId, CancellationToken.None));
        Assert.False(await verify.Set<RolePermissionRemovalOverrideEntity>()
            .AnyAsync(o => o.SiteId == siteId, CancellationToken.None));
    }

    /// <summary>A role name that does not exist on this site is refused with `Operator.RoleNotFound`,
    /// and writes nothing.</summary>
    [Fact]
    public async Task HandleAsync_WithARoleNameThatDoesNotExist_Refuses()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using var db = fixture.CreateDbContext();
        var roleRepository = new RoleRepository(db, new UuidV7Generator(), new FixedClock(Now));
        var handler = new RemoveRolePermissionsAsOwnerHandler(roleRepository);

        var result = await handler.HandleAsync(
            new RemoveRolePermissionsAsOwner(siteId, "SuperAdmin", [Permission.SiteConfigure.Value], "owner-sub", "a real reason"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Operator.RoleNotFound", result.Error!.Value.Code);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
