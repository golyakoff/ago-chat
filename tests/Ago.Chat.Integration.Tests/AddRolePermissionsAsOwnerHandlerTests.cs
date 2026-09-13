using System.Text.Json;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Application.UseCases.AddRolePermissionsAsOwner;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-76`: the platform owner's own role-permission write, run against a real Postgres rather than
/// <c>Ago.Chat.Application.Tests</c>' own faked <c>IRoleRepository</c> - the same reason
/// <see cref="RoleRepositoryTests"/> exists at all for <c>AddPermissionsAsync</c> itself
/// (<see cref="RoleRepositoryTests"/>'s own remarks: the fakes have no outbox to stage into).
///
/// <para><b>This file's own headline proof.</b> <c>AddPermissionsAsync</c>'s own doc comment promises a
/// permission change "takes effect for every operator already holding that role... without them
/// signing out and back in" via the <c>RoleAssignmentsChanged</c> publish - already proven true of
/// <c>AddPermissionsAsync</c> directly by <see cref="RoleRepositoryTests"/>. What was not yet proven is
/// that the *owner's own write path* - <see cref="AddRolePermissionsAsOwnerHandler"/>, the thin wrapper
/// this item adds - actually reaches that method with the right arguments end to end, rather than
/// merely typechecking against a fake. This item's own Done-when asks for exactly that: "proven end to
/// end, not assumed from the existing method's own contract."</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AddRolePermissionsAsOwnerHandlerTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now =
        new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    /// <summary>The two real gaps this item was found from, closed through this handler rather than a
    /// hand-run `UPDATE` - `channel:manage` missing from a live `Admin` role, and reaching a
    /// currently-signed-in operator's own projection without them signing out and back in.</summary>
    [Fact]
    public async Task HandleAsync_AddingAMissingPermission_WritesTheRole_AndReachesAnAlreadySignedInOperator()
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
                // The exact live-deployment gap this item was found from, minus channel:manage.
                Permissions = [Permission.SiteConfigure.Value, Permission.SiteManageOperators.Value, Permission.AttachmentDelete.Value],
            });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var roleRepository = new RoleRepository(db, new UuidV7Generator(), new FixedClock(Now));
            var handler = new AddRolePermissionsAsOwnerHandler(roleRepository);

            var result = await handler.HandleAsync(
                new AddRolePermissionsAsOwner(siteId, "Admin", [Permission.ChannelManage.Value]), CancellationToken.None);

            Assert.True(result.IsSuccess);
        }

        await using var verify = fixture.CreateDbContext();
        var role = await verify.Set<RoleRecord>().AsNoTracking().SingleAsync(r => r.Id == roleId, CancellationToken.None);
        Assert.Contains(Permission.ChannelManage.Value, role.Permissions);

        // The outbox row a currently-linked operator's own projection would read to catch up without
        // signing out and back in - the same row RoleRepositoryTests' own single-holder test proves for
        // AddPermissionsAsync called directly, here proven for the owner's own wrapper around it.
        var outboxRow = await verify.Set<OutboxMessage>().SingleAsync(
            o => o.Type == nameof(RoleAssignmentsChanged) && o.PartitionKey == externalSubjectId, CancellationToken.None);
        var contract = JsonSerializer.Deserialize<RoleAssignmentsChanged>(outboxRow.Payload)!;
        Assert.Equal(siteId.Value, contract.SiteId);
        Assert.Contains(Permission.ChannelManage.Value, contract.Permissions);
        Assert.Null(outboxRow.PublishedAt);
    }

    /// <summary>Fails-before, against a real database: an unknown permission never reaches `roles` or
    /// the outbox at all - refused entirely in the handler, before <c>IRoleRepository.AddPermissionsAsync</c>
    /// is ever called. An operator holds the role here (unlike a bare role-only fixture) specifically so
    /// the outbox assertion can be scoped to that operator's own external subject
    /// (<c>o.PartitionKey == externalSubjectId</c>) rather than asking "does any RoleAssignmentsChanged
    /// row exist anywhere in this table" - <c>Ago.Chat.Integration.Tests</c> runs many tests against one
    /// shared Postgres database, so an unscoped existence check fails from a completely unrelated test's
    /// own, entirely correct, publish - found live, the hard way, by this test's own first draft.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WithAnUnknownPermission_WritesNothing_ToTheRoleOrTheOutbox()
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
            var handler = new AddRolePermissionsAsOwnerHandler(roleRepository);

            var result = await handler.HandleAsync(
                new AddRolePermissionsAsOwner(siteId, "Admin", ["not:a-real-permission"]), CancellationToken.None);

            Assert.True(result.IsFailure);
            Assert.Equal("Role.PermissionUnknown", result.Error!.Value.Code);
        }

        await using var verify = fixture.CreateDbContext();
        var role = await verify.Set<RoleRecord>().AsNoTracking().SingleAsync(r => r.Id == roleId, CancellationToken.None);
        Assert.Equal([Permission.SiteConfigure.Value], role.Permissions);
        Assert.False(await verify.Set<OutboxMessage>().AnyAsync(
            o => o.Type == nameof(RoleAssignmentsChanged) && o.PartitionKey == externalSubjectId, CancellationToken.None));
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
