using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-102`: <see cref="RoleRepository.AddPermissionsAsync"/> against a real Postgres - the raw
/// <c>unnest(permissions || @new)</c> merge that class's own remarks describe, proven to actually
/// produce the right `text[]` rather than merely typechecking. Nothing in
/// <c>Ago.Chat.Application.Tests</c> exercises this SQL at all (the fakes there never touch a
/// database), and <c>OwnerModuleEndpointsTests</c>'s own test host deliberately configures
/// <c>IModulePermissionsProvider</c> to return <c>ModulePermissionSet.Empty</c> for every key (that
/// file's own remarks: avoiding cross-test pollution of its shared fixture's "Admin" role) - so this
/// file is the only place the merge itself runs against a real `roles` row.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RoleRepositoryTests(PostgresFixture fixture)
{
    [Fact]
    public async Task AddPermissionsAsync_AddsEveryNewPermission_ToAnExistingRolesSet()
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
                Name = "Operator",
                Permissions = [Permission.ConversationRead.Value],
            });
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new RoleRepository(db);
            await repository.AddPermissionsAsync(
                siteId, "Operator", [Permission.BookingConfirm.Value, Permission.BookingReject.Value],
                CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        var role = await verify.Set<RoleRecord>().AsNoTracking().SingleAsync(r => r.Id == roleId, CancellationToken.None);
        Assert.Equal(
            new[] { Permission.BookingConfirm.Value, Permission.BookingReject.Value, Permission.ConversationRead.Value }
                .OrderBy(p => p, StringComparer.Ordinal),
            role.Permissions.OrderBy(p => p, StringComparer.Ordinal));
    }

    /// <summary>`23-102`'s own headline claim, proven against the database rather than a fake: a
    /// repeated call - a re-grant, a revoke-then-re-grant cycle - adds nothing the second time. This is
    /// the exact operation the author's own re-grant performs on an already-granted site.</summary>
    [Fact]
    public async Task AddPermissionsAsync_CalledTwiceWithTheSamePermissions_IsIdempotent()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var roleId = Guid.NewGuid();
        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            seed.Roles.Add(new RoleRecord { Id = roleId, SiteId = siteId, Name = "Admin", Permissions = [] });
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new RoleRepository(db);
            await repository.AddPermissionsAsync(siteId, "Admin", [Permission.CalendarConfigure.Value], CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new RoleRepository(db);
            await repository.AddPermissionsAsync(siteId, "Admin", [Permission.CalendarConfigure.Value], CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        var role = await verify.Set<RoleRecord>().AsNoTracking().SingleAsync(r => r.Id == roleId, CancellationToken.None);
        Assert.Equal([Permission.CalendarConfigure.Value], role.Permissions);
    }

    /// <summary>No role by that name is a no-op, not an error - the same "nothing to resolve against"
    /// shape a site that has not yet had "Operator"/"Admin" seeded (there is none today, but nothing in
    /// this repository's contract assumes there always will be) would hit.</summary>
    [Fact]
    public async Task AddPermissionsAsync_WhenNoRoleByThatNameExists_DoesNothing_AndDoesNotThrow()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using var db = fixture.CreateDbContext();
        var repository = new RoleRepository(db);
        var exception = await Record.ExceptionAsync(() => repository.AddPermissionsAsync(
            siteId, "Operator", [Permission.BookingConfirm.Value], CancellationToken.None));

        Assert.Null(exception);
    }

    /// <summary>An empty permission list never reaches the database at all - <see cref="RoleRepository.AddPermissionsAsync"/>'s
    /// own early return. Proven here by the role's permission set staying exactly what it started as,
    /// on a site whose "Operator" role does exist (so a silent no-op could not be mistaken for the
    /// "no such role" case above).</summary>
    [Fact]
    public async Task AddPermissionsAsync_WithNoPermissions_LeavesTheRoleUnchanged()
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
                Name = "Operator",
                Permissions = [Permission.ConversationRead.Value],
            });
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new RoleRepository(db);
            await repository.AddPermissionsAsync(siteId, "Operator", [], CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        var role = await verify.Set<RoleRecord>().AsNoTracking().SingleAsync(r => r.Id == roleId, CancellationToken.None);
        Assert.Equal([Permission.ConversationRead.Value], role.Permissions);
    }
}
