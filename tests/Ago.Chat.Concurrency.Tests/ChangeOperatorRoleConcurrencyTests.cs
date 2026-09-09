using Ago.Chat.Application.UseCases.ChangeOperatorRole;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>
/// `23-72`'s own last-administrator guard, proven at the only level that can prove it - the identical
/// shape <see cref="RemoveOperatorConcurrencyTests"/> already established for `23-26`'s sibling
/// invariant, over the same primitive (<c>IPermissionChecker.CountNonRemovedHoldersAsync</c>'s own
/// `SELECT ... FOR UPDATE` lock on `sites`), reused here rather than duplicated
/// (<see cref="ChangeOperatorRoleHandler"/>'s own remarks). A site with exactly two
/// `site:manage_operators` holders, each concurrently demoting the *other* to `"Operator"`: with a
/// cached or out-of-transaction count both demotions would see "two holders, safe to demote" and both
/// would succeed, leaving nobody who can sign in and manage operators.
/// </summary>
[Collection(ConcurrencyCollection.Name)]
public sealed class ChangeOperatorRoleConcurrencyTests(ConcurrencyTestFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task ConcurrentDemotions_OfASitesLastTwoAdministrators_ExactlyOneSucceeds_AndOneAdministratorRemains()
    {
        var (siteId, adminAId, adminBId) = await SeedSiteWithTwoAdministratorsAsync();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var demoteBByA = Task.Run(async () =>
        {
            await gate.Task;
            return await DemoteAsync(siteId, requestedBy: adminAId, target: adminBId);
        });
        var demoteAByB = Task.Run(async () =>
        {
            await gate.Task;
            return await DemoteAsync(siteId, requestedBy: adminBId, target: adminAId);
        });

        gate.SetResult();
        var results = await Task.WhenAll(demoteBByA, demoteAByB);

        var successes = results.Count(r => r.IsSuccess);
        var refusedAsLastManager = results.Count(r => r.IsFailure && r.Error!.Value.Code == "Operator.IsLastManager");

        output.WriteLine($"successes={successes}; refusedAsLastManager={refusedAsLastManager}");

        // Exactly one winner, exactly one refusal - not "at most one", not "roughly one". The loser's
        // own transaction rolled back with nothing committed (ChangeOperatorRoleHandler's own remarks),
        // so no third outcome (an unhandled exception, a partial write) is acceptable either.
        Assert.Equal(1, successes);
        Assert.Equal(1, refusedAsLastManager);

        await using var verify = fixture.CreateDbContext();
        var administratorRoleIds = verify.Roles.AsNoTracking()
            .Where(r => r.SiteId == siteId && r.Permissions.Contains(Permission.SiteManageOperators.Value))
            .Select(r => r.Id);
        var remainingAdministrators = await verify.OperatorRoles.AsNoTracking()
            .Where(link => administratorRoleIds.Contains(link.RoleId))
            .Join(verify.Operators.AsNoTracking(), link => link.OperatorId, o => o.Id, (link, o) => o)
            .Where(o => o.SiteId == siteId && o.RemovedAt == null)
            .CountAsync();

        // The invariant this whole item exists for, checked against the database, not the in-process
        // outcome count: the site was never left with nobody who can manage operators.
        Assert.Equal(1, remainingAdministrators);

        // The audit record commits with the winner's own role swap and nowhere else - the loser's own
        // transaction rolled back before RoleChangeRecordRepository.RecordAsync was ever reached
        // (ChangeOperatorRoleHandler's own remarks: the count check runs before either write), so
        // exactly one row, never two and never zero, is the same "checked against the database" proof
        // as the administrator count above, for the write rule 4 exists to protect.
        var recordCount = await verify.RoleChangeRecords.AsNoTracking().CountAsync(r => r.SiteId == siteId);
        Assert.Equal(1, recordCount);
    }

    private async Task<Result> DemoteAsync(SiteId siteId, OperatorId requestedBy, OperatorId target)
    {
        await using var db = fixture.CreateDbContext();
        var operators = new OperatorRepository(db);
        var roles = new RoleRepository(db, new UuidV7Generator(), new SystemClock());
        var operatorRoles = new OperatorRoleRepository(db);
        var permissions = new PermissionChecker(db);
        var sites = new SiteRepository(db);
        var unitOfWork = new EfUnitOfWork(db);
        var roleChangeRecords = new RoleChangeRecordRepository(db);
        var outbox = new EfOutboxWriter<AgoChatDbContext>(db);
        var handler = new ChangeOperatorRoleHandler(
            operators, roles, operatorRoles, permissions, sites, unitOfWork, roleChangeRecords, outbox,
            new UuidV7Generator(), new SystemClock());
        return await handler.HandleAsync(new ChangeOperatorRole(requestedBy, siteId, target, "Operator"), CancellationToken.None);
    }

    private async Task<(SiteId SiteId, OperatorId AdminAId, OperatorId AdminBId)> SeedSiteWithTwoAdministratorsAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var adminRoleId = Guid.NewGuid();
        var operatorRoleId = Guid.NewGuid();
        var adminAId = new OperatorId(Guid.NewGuid());
        var adminBId = new OperatorId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Roles.Add(new RoleRecord { Id = adminRoleId, SiteId = siteId, Name = "Admin", Permissions = [Permission.SiteManageOperators.Value] });
        db.Roles.Add(new RoleRecord { Id = operatorRoleId, SiteId = siteId, Name = "Operator", Permissions = [Permission.ConversationRead.Value] });
        db.Operators.Add(new Operator(adminAId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId: "admin-a"));
        db.Operators.Add(new Operator(adminBId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId: "admin-b"));
        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = adminAId, RoleId = adminRoleId });
        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = adminBId, RoleId = adminRoleId });
        await db.SaveChangesAsync(CancellationToken.None);

        return (siteId, adminAId, adminBId);
    }
}
