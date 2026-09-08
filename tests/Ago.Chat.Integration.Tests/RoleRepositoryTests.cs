using System.Text.Json;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
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
///
/// <para><b>`23-104`: also the only place the publish side of that same write runs against a real
/// database.</b> `Ago.Chat.Application.Tests`' own <c>FakeRoleRepository</c> stops at the interface -
/// it has no outbox to stage into, so it cannot prove a single row was enqueued, let alone one per
/// holder of the role. The tests below seed operators directly against the fixture's own
/// <see cref="AgoChatDbContext"/>, the same "no publisher ever ran, write the row by hand" shape
/// <c>RoleAssignmentProjectionBackfillTests</c>' own remarks describe, then call
/// <see cref="RoleRepository.AddPermissionsAsync"/> and read the outbox table back.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RoleRepositoryTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now =
        new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

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
            var repository = new RoleRepository(db, new UuidV7Generator(), new FixedClock(Now));
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
            var repository = new RoleRepository(db, new UuidV7Generator(), new FixedClock(Now));
            await repository.AddPermissionsAsync(siteId, "Admin", [Permission.CalendarConfigure.Value], CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new RoleRepository(db, new UuidV7Generator(), new FixedClock(Now.AddMinutes(1)));
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
        var repository = new RoleRepository(db, new UuidV7Generator(), new FixedClock(Now));
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
            var repository = new RoleRepository(db, new UuidV7Generator(), new FixedClock(Now));
            await repository.AddPermissionsAsync(siteId, "Operator", [], CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        var role = await verify.Set<RoleRecord>().AsNoTracking().SingleAsync(r => r.Id == roleId, CancellationToken.None);
        Assert.Equal([Permission.ConversationRead.Value], role.Permissions);
    }

    /// <summary>
    /// `23-104`'s own headline claim: seeding a module's permissions is not just a `roles` write, it is
    /// an outbox write too - one <see cref="RoleAssignmentsChanged"/> row, staged in the same call, not
    /// left for somebody to notice is missing and run <c>RoleAssignmentProjectionBackfill</c> by hand.
    /// </summary>
    [Fact]
    public async Task AddPermissionsAsync_ForASingleHolder_StagesARoleAssignmentsChangedRow_CarryingTheFullPermissionSet()
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
                Permissions = [Permission.SiteConfigure.Value],
            });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new RoleRepository(db, new UuidV7Generator(), new FixedClock(Now));
            await repository.AddPermissionsAsync(siteId, "Admin", [Permission.CalendarConfigure.Value], CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        var outboxRow = await verify.Set<OutboxMessage>().SingleAsync(
            o => o.Type == nameof(RoleAssignmentsChanged) && o.PartitionKey == externalSubjectId, CancellationToken.None);
        var contract = JsonSerializer.Deserialize<RoleAssignmentsChanged>(outboxRow.Payload)!;
        Assert.Equal(siteId.Value, contract.SiteId);
        Assert.Equal(
            new[] { Permission.SiteConfigure.Value, Permission.CalendarConfigure.Value }.OrderBy(p => p, StringComparer.Ordinal),
            contract.Permissions.OrderBy(p => p, StringComparer.Ordinal));
        Assert.Null(outboxRow.PublishedAt);
    }

    /// <summary>
    /// The item's own sharpest example: two operators hold "Operator" on the same site, and a module
    /// grant seeds a permission into that role. Both learn - not only whichever operator's own request
    /// happened to trigger the grant, since the projection is keyed by subject and the role's permissions
    /// are shared by everyone who holds it. A publish scoped to one caller would leave the second
    /// operator exactly as stale as the tenant this item was found on.
    /// </summary>
    [Fact]
    public async Task AddPermissionsAsync_WithTwoOperatorsHoldingTheRole_StagesARowForEachOfThem()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var firstOperatorId = new OperatorId(Guid.NewGuid());
        var secondOperatorId = new OperatorId(Guid.NewGuid());
        var firstSubject = $"sub-{Guid.NewGuid():N}";
        var secondSubject = $"sub-{Guid.NewGuid():N}";
        var roleId = Guid.NewGuid();

        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            seed.Operators.Add(new Operator(firstOperatorId, siteId, OperatorStatus.Offline, capacity: 5, firstSubject));
            seed.Operators.Add(new Operator(secondOperatorId, siteId, OperatorStatus.Offline, capacity: 5, secondSubject));
            seed.Roles.Add(new RoleRecord
            {
                Id = roleId,
                SiteId = siteId,
                Name = "Operator",
                Permissions = [Permission.ConversationRead.Value],
            });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = firstOperatorId, RoleId = roleId });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = secondOperatorId, RoleId = roleId });
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new RoleRepository(db, new UuidV7Generator(), new FixedClock(Now));
            await repository.AddPermissionsAsync(
                siteId, "Operator", [Permission.BookingConfirm.Value, Permission.CustomerRead.Value], CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        foreach (var subject in new[] { firstSubject, secondSubject })
        {
            var outboxRow = await verify.Set<OutboxMessage>().SingleAsync(
                o => o.Type == nameof(RoleAssignmentsChanged) && o.PartitionKey == subject, CancellationToken.None);
            var contract = JsonSerializer.Deserialize<RoleAssignmentsChanged>(outboxRow.Payload)!;
            Assert.Equal(
                new[] { Permission.ConversationRead.Value, Permission.BookingConfirm.Value, Permission.CustomerRead.Value }
                    .OrderBy(p => p, StringComparer.Ordinal),
                contract.Permissions.OrderBy(p => p, StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// The account owner's own shape (<c>SiteRegistrationRepository</c>'s own remarks): one operator
    /// holding both seeded roles. Seeding a permission into either one must publish the *union* of both,
    /// not just whichever role's own permission list this call happened to touch - the projection carries
    /// one full snapshot per subject, never a per-role fragment.
    /// </summary>
    [Fact]
    public async Task AddPermissionsAsync_ForAnOperatorHoldingTwoRoles_StagesTheUnionOfBoth()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var externalSubjectId = $"sub-{Guid.NewGuid():N}";
        var operatorRoleId = Guid.NewGuid();
        var adminRoleId = Guid.NewGuid();

        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            seed.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId));
            seed.Roles.Add(new RoleRecord
            {
                Id = operatorRoleId,
                SiteId = siteId,
                Name = "Operator",
                Permissions = [Permission.ConversationRead.Value],
            });
            seed.Roles.Add(new RoleRecord
            {
                Id = adminRoleId,
                SiteId = siteId,
                Name = "Admin",
                Permissions = [Permission.SiteConfigure.Value],
            });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = operatorRoleId });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = adminRoleId });
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new RoleRepository(db, new UuidV7Generator(), new FixedClock(Now));
            await repository.AddPermissionsAsync(siteId, "Admin", [Permission.CalendarConfigure.Value], CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        var outboxRow = await verify.Set<OutboxMessage>().SingleAsync(
            o => o.Type == nameof(RoleAssignmentsChanged) && o.PartitionKey == externalSubjectId, CancellationToken.None);
        var contract = JsonSerializer.Deserialize<RoleAssignmentsChanged>(outboxRow.Payload)!;
        Assert.Equal(
            new[] { Permission.ConversationRead.Value, Permission.SiteConfigure.Value, Permission.CalendarConfigure.Value }
                .OrderBy(p => p, StringComparer.Ordinal),
            contract.Permissions.OrderBy(p => p, StringComparer.Ordinal));
    }

    /// <summary>
    /// An operator with an unredeemed invite has no linked external identity - <see langword="null"/>
    /// <see cref="Operator.ExternalSubjectId"/>, the same shape <c>RoleAssignmentProjectionBackfill</c>'s
    /// own candidate list and <c>RemoveOperatorHandler</c>'s own publisher both exclude. They have no
    /// projection row anywhere to correct, so this must not throw trying to name a subject that does not
    /// exist - it skips them, and only them.
    /// </summary>
    [Fact]
    public async Task AddPermissionsAsync_WithAHolderWhoHasNoLinkedIdentity_SkipsThem_AndDoesNotThrow()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var linkedOperatorId = new OperatorId(Guid.NewGuid());
        var unlinkedOperatorId = new OperatorId(Guid.NewGuid());
        var linkedSubject = $"sub-{Guid.NewGuid():N}";
        var roleId = Guid.NewGuid();

        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            seed.Operators.Add(new Operator(linkedOperatorId, siteId, OperatorStatus.Offline, capacity: 5, linkedSubject));
            seed.Operators.Add(new Operator(unlinkedOperatorId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId: null));
            seed.Roles.Add(new RoleRecord
            {
                Id = roleId,
                SiteId = siteId,
                Name = "Operator",
                Permissions = [Permission.ConversationRead.Value],
            });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = linkedOperatorId, RoleId = roleId });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = unlinkedOperatorId, RoleId = roleId });
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new RoleRepository(db, new UuidV7Generator(), new FixedClock(Now));
            var exception = await Record.ExceptionAsync(() => repository.AddPermissionsAsync(
                siteId, "Operator", [Permission.BookingConfirm.Value], CancellationToken.None));
            Assert.Null(exception);
        }

        await using var verify = fixture.CreateDbContext();
        var rows = await verify.Set<OutboxMessage>()
            .Where(o => o.Type == nameof(RoleAssignmentsChanged) && o.PartitionKey == linkedSubject)
            .ToListAsync(CancellationToken.None);
        Assert.Single(rows);
    }

    /// <summary>
    /// A removed operator still holds the `operator_roles` link (nothing deletes it), but must not be
    /// republished as still holding the role - `RemoveOperatorHandler`'s own event, published at removal
    /// time, is the truthful fact for that subject.
    /// </summary>
    [Fact]
    public async Task AddPermissionsAsync_WithARemovedHolder_SkipsThem()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var removedOperatorId = new OperatorId(Guid.NewGuid());
        var removedSubject = $"sub-{Guid.NewGuid():N}";
        var roleId = Guid.NewGuid();

        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            seed.Operators.Add(new Operator(
                removedOperatorId, siteId, OperatorStatus.Offline, capacity: 5, removedSubject,
                holdsSeat: false, removedAt: Now.AddDays(-1)));
            seed.Roles.Add(new RoleRecord
            {
                Id = roleId,
                SiteId = siteId,
                Name = "Operator",
                Permissions = [Permission.ConversationRead.Value],
            });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = removedOperatorId, RoleId = roleId });
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new RoleRepository(db, new UuidV7Generator(), new FixedClock(Now));
            await repository.AddPermissionsAsync(siteId, "Operator", [Permission.BookingConfirm.Value], CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        var anyRow = await verify.Set<OutboxMessage>().AnyAsync(
            o => o.Type == nameof(RoleAssignmentsChanged) && o.PartitionKey == removedSubject, CancellationToken.None);
        Assert.False(anyRow);
    }

    /// <summary>
    /// Idempotent both ways, proven with real before/after counts rather than asserted: a re-grant of an
    /// identical permission set still stages a fresh row per holder (`RoleAssignmentProjectionStore`'s own
    /// unconditional full replace makes that harmless, not a duplicate to avoid), which is also the repair
    /// path for a site whose projection went stale before this item existed - re-granting an already-held
    /// module now fixes it, with no separate tool.
    /// </summary>
    [Fact]
    public async Task AddPermissionsAsync_CalledTwice_StagesARowEachTime_RealBeforeAndAfterCounts()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var externalSubjectId = $"sub-{Guid.NewGuid():N}";
        var roleId = Guid.NewGuid();

        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            seed.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId));
            seed.Roles.Add(new RoleRecord { Id = roleId, SiteId = siteId, Name = "Admin", Permissions = [] });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        async Task<int> CountOutboxRowsAsync()
        {
            await using var verify = fixture.CreateDbContext();
            return await verify.Set<OutboxMessage>().CountAsync(
                o => o.Type == nameof(RoleAssignmentsChanged) && o.PartitionKey == externalSubjectId);
        }

        var beforeAnyCall = await CountOutboxRowsAsync();

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new RoleRepository(db, new UuidV7Generator(), new FixedClock(Now));
            await repository.AddPermissionsAsync(siteId, "Admin", [Permission.CalendarConfigure.Value], CancellationToken.None);
        }

        var afterFirstCall = await CountOutboxRowsAsync();

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new RoleRepository(db, new UuidV7Generator(), new FixedClock(Now.AddMinutes(1)));
            await repository.AddPermissionsAsync(siteId, "Admin", [Permission.CalendarConfigure.Value], CancellationToken.None);
        }

        var afterSecondCall = await CountOutboxRowsAsync();

        Assert.Equal(0, beforeAnyCall);
        Assert.Equal(1, afterFirstCall);
        Assert.Equal(2, afterSecondCall);

        await using var verify = fixture.CreateDbContext();
        var rows = await verify.Set<OutboxMessage>()
            .Where(o => o.Type == nameof(RoleAssignmentsChanged) && o.PartitionKey == externalSubjectId)
            .ToListAsync(CancellationToken.None);
        Assert.All(rows, row =>
        {
            var contract = JsonSerializer.Deserialize<RoleAssignmentsChanged>(row.Payload)!;
            Assert.Equal([Permission.CalendarConfigure.Value], contract.Permissions);
        });
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
