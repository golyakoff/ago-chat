using Ago.Chat.Application.UseCases.RemoveOperator;
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
/// `23-26`'s own Done-when, at the only level that can prove it - the backlog item's own words: "this
/// assertion... is worthless as anything but a demonstration" on real Postgres with two real
/// connections. A site with exactly two `site:manage_operators` holders, each concurrently removing the
/// *other*: with a cached or out-of-transaction count both removals would see "two holders, safe to
/// remove" and both would succeed, leaving nobody. What actually makes this safe is
/// <c>PermissionChecker.CountNonRemovedHoldersAsync</c>'s own `SELECT ... FOR UPDATE` lock on `sites`
/// serializing the two attempts - the identical mechanism, and the identical test shape, as
/// <c>OperatorInviteSeatLimitConcurrencyTests</c> already established for the sibling seat-limit race:
/// each attempt gets its own <see cref="AgoChatDbContext"/>, its own real connection, no shared state
/// but the database itself.
/// </summary>
[Collection(ConcurrencyCollection.Name)]
public sealed class RemoveOperatorConcurrencyTests(ConcurrencyTestFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task ConcurrentRemovals_OfASitesLastTwoManagers_ExactlyOneSucceeds_AndOneManagerRemains()
    {
        var (siteId, managerAId, managerBId) = await SeedSiteWithTwoManagersAsync();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var removeBByA = Task.Run(async () =>
        {
            await gate.Task;
            return await RemoveAsync(siteId, requestedBy: managerAId, target: managerBId);
        });
        var removeAByB = Task.Run(async () =>
        {
            await gate.Task;
            return await RemoveAsync(siteId, requestedBy: managerBId, target: managerAId);
        });

        gate.SetResult();
        var results = await Task.WhenAll(removeBByA, removeAByB);

        var successes = results.Count(r => r.IsSuccess);
        var refusedAsLastManager = results.Count(r => r.IsFailure && r.Error!.Value.Code == "Operator.IsLastManager");

        output.WriteLine($"successes={successes}; refusedAsLastManager={refusedAsLastManager}");

        // Exactly one winner, exactly one refusal - not "at most one", not "roughly one". The loser's
        // own transaction rolled back with nothing committed (RemoveOperatorHandler's own remarks), so
        // no third outcome (an unhandled exception, a partial write) is acceptable either.
        Assert.Equal(1, successes);
        Assert.Equal(1, refusedAsLastManager);

        await using var verify = fixture.CreateDbContext();
        var remainingManagers = await verify.Operators.AsNoTracking()
            .Where(o => o.SiteId == siteId && o.RemovedAt == null)
            .CountAsync();

        // The invariant this whole item exists for, checked against the database, not the in-process
        // outcome count: the site was never left with nobody who can manage operators.
        Assert.Equal(1, remainingManagers);
    }

    private async Task<Result> RemoveAsync(SiteId siteId, OperatorId requestedBy, OperatorId target)
    {
        await using var db = fixture.CreateDbContext();
        var operators = new OperatorRepository(db);
        var permissions = new PermissionChecker(db);
        var unitOfWork = new EfUnitOfWork(db);
        var outbox = new EfOutboxWriter<AgoChatDbContext>(db);
        var handler = new RemoveOperatorHandler(operators, permissions, unitOfWork, outbox, new UuidV7Generator(), new SystemClock());
        return await handler.HandleAsync(new RemoveOperator(requestedBy, siteId, target), CancellationToken.None);
    }

    private async Task<(SiteId SiteId, OperatorId ManagerAId, OperatorId ManagerBId)> SeedSiteWithTwoManagersAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var roleId = Guid.NewGuid();
        var managerAId = new OperatorId(Guid.NewGuid());
        var managerBId = new OperatorId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Roles.Add(new RoleRecord { Id = roleId, SiteId = siteId, Name = "Admin", Permissions = [Permission.SiteManageOperators.Value] });
        db.Operators.Add(new Operator(managerAId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId: "manager-a"));
        db.Operators.Add(new Operator(managerBId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId: "manager-b"));
        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = managerAId, RoleId = roleId });
        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = managerBId, RoleId = roleId });
        await db.SaveChangesAsync(CancellationToken.None);

        return (siteId, managerAId, managerBId);
    }
}
