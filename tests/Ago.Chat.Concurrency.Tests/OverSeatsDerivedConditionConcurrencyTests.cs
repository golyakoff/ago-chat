using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetSeatAssignmentSummary;
using Ago.Chat.Application.UseCases.OperatorRoleSeats;
using Ago.Chat.Application.UseCases.ToggleOperatorSeat;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>
/// `13-03`'s own Done-when: the over-seats condition (`assigned-seat count > seat_limit`) is computed
/// correctly under "a downgrade landing at the same moment as an operator toggling another operator's
/// seat" - proven, not asserted from the query looking right.
///
/// <para><b>Why this is not a lock-contention test the way <see cref="OperatorInviteSeatLimitConcurrencyTests"/>
/// is.</b> A downgrade writes <c>sites.seat_limit</c>; a seat toggle writes one <c>operator_roles</c>
/// row (`25-170`) - two independent rows, never contended against each other, so there is no race to
/// serialize the way two redemptions racing for the same seat need `SELECT ... FOR UPDATE` to serialize.
/// What this item's own Scope actually calls a derived, read-time condition needs proven is narrower and
/// just as real: that <see cref="GetSeatAssignmentSummaryHandler"/>'s own read - one query against
/// `operator_roles`, one against `sites`, no shared lock between them - never reports a torn or stale
/// combination once both writes have actually committed, and that a storm of concurrent seat toggles
/// racing <see cref="ToggleOperatorSeatHandler"/>'s own capacity guard against a seat_limit that changes
/// mid-storm neither deadlocks nor lets the held-seat count drift from what the database actually
/// holds.</para>
/// </summary>
[Collection(ConcurrencyCollection.Name)]
public sealed class OverSeatsDerivedConditionConcurrencyTests(ConcurrencyTestFixture fixture, ITestOutputHelper output)
{
    private const string OperatorRoleName = "Operator";

    [Fact]
    public async Task ADowngrade_RacingConcurrentSeatToggles_LeavesTheDerivedReadConsistentWithWhatWasActuallyWritten()
    {
        const int initialSeatLimit = 10;
        const int downgradedSeatLimit = 3;
        const int holdingOperators = 6; // already over what the downgrade will allow
        const int seatlessOperators = 6; // each concurrently trying to toggle on

        var (siteId, roleId) = await SeedSiteAsync(initialSeatLimit);
        var holding = await SeedOperatorsAsync(siteId, roleId, holdingOperators, holdsSeat: true);
        var seatless = await SeedOperatorsAsync(siteId, roleId, seatlessOperators, holdsSeat: false);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // One task applies the downgrade directly (the write `SubscriptionRenewalApplier` makes once a
        // deferred downgrade's own renewal actually lands - EF's `Entry(...).Property(...).CurrentValue`
        // is this codebase's own established way to simulate a private-setter write in a test,
        // `OperatorInviteSeatLimitConcurrencyTests`' own precedent).
        var downgradeTask = Task.Run(async () =>
        {
            await gate.Task;
            await using var db = fixture.CreateDbContext();
            var site = await db.Sites.SingleAsync(s => s.Id == siteId);
            db.Entry(site).Property(nameof(Site.SeatLimit)).CurrentValue = downgradedSeatLimit;
            await db.SaveChangesAsync();
        });

        // Every seatless operator races the downgrade to toggle their own seat on - some may see the
        // old seat_limit (10, plenty of room), some may see the new one (3, already exceeded by the
        // six operators already holding a seat) depending on real interleaving with the write above.
        var toggleTasks = seatless.Select(operatorId => Task.Run(async () =>
        {
            await gate.Task;
            await using var db = fixture.CreateDbContext();
            var operatorRoles = new OperatorRoleRepository(db);
            // `25-181`: a real OwnerSeatGrantStore with nothing seeded reads 0 - this test keeps
            // exercising the identical billing-only seat limit it always has.
            var roleSeatCapacity = new OperatorRoleSeatCapacity(
                operatorRoles, new SiteRepository(db), new OwnerSeatGrantStore(db), new Ago.Platform.Hosting.SystemClock());
            var handler = new ToggleOperatorSeatHandler(
                new OperatorRepository(db), operatorRoles, new AlwaysAllowPermissionChecker(), new EfUnitOfWork(db), roleSeatCapacity);
            return await handler.HandleAsync(
                new ToggleOperatorSeat(new OperatorId(Guid.NewGuid()), siteId, operatorId, OperatorRoleName, true), CancellationToken.None);
        })).ToList();

        gate.SetResult();
        await downgradeTask;
        var toggleOutcomes = await Task.WhenAll(toggleTasks);

        var toggledOn = toggleOutcomes.Count(r => r.IsSuccess);
        output.WriteLine($"toggledOn={toggledOn} of {seatlessOperators}; downgraded seat_limit={downgradedSeatLimit}");

        // The ground truth, read independently of the handler under test - what the database actually
        // holds after every write above has committed.
        await using var verify = fixture.CreateDbContext();
        var actualHeldSeats = await verify.OperatorRoles.AsNoTracking()
            .Where(link => link.RoleId == roleId && link.HoldsSeat)
            .Join(verify.Operators.AsNoTracking(), link => link.OperatorId, o => o.Id, (link, o) => o)
            .CountAsync(o => o.SiteId == siteId && o.RemovedAt == null);
        var actualSeatLimit = (await verify.Sites.AsNoTracking().SingleAsync(s => s.Id == siteId)).SeatLimit;
        Assert.Equal(downgradedSeatLimit, actualSeatLimit);
        Assert.Equal(holdingOperators + toggledOn, actualHeldSeats);

        // The derived read itself, against the same real data - must match the ground truth exactly,
        // not merely "close" or "eventually" - and since six operators already held a seat before the
        // downgrade to three, over-seats is true no matter how many of the racing toggles happened to
        // land before or after it.
        await using var summaryDb = fixture.CreateDbContext();
        var summaryHandler = new GetSeatAssignmentSummaryHandler(
            new OperatorRoleRepository(summaryDb), new SiteRepository(summaryDb), new AlwaysAllowPermissionChecker());
        var summary = await summaryHandler.HandleAsync(
            new GetSeatAssignmentSummary(new OperatorId(Guid.NewGuid()), siteId), CancellationToken.None);

        Assert.True(summary.IsSuccess);
        var operatorRoleSummary = summary.Value.Roles.Single(r => r.RoleName == OperatorRoleName);
        Assert.Equal(actualHeldSeats, operatorRoleSummary.HeldSeats);
        Assert.Equal(actualSeatLimit, operatorRoleSummary.Limit);
        Assert.True(operatorRoleSummary.OverLimit);
        Assert.True(operatorRoleSummary.HeldSeats > operatorRoleSummary.Limit);
    }

    private async Task<(SiteId SiteId, Guid RoleId)> SeedSiteAsync(int seatLimit)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var roleId = Guid.NewGuid();
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [], seatLimit: seatLimit));
        db.Roles.Add(new RoleRecord { Id = roleId, SiteId = siteId, Name = OperatorRoleName, Permissions = [Permission.ConversationRead.Value] });
        await db.SaveChangesAsync();
        return (siteId, roleId);
    }

    private async Task<List<OperatorId>> SeedOperatorsAsync(SiteId siteId, Guid roleId, int count, bool holdsSeat)
    {
        var ids = new List<OperatorId>();
        await using var db = fixture.CreateDbContext();
        for (var i = 0; i < count; i++)
        {
            var operatorId = new OperatorId(Guid.NewGuid());
            db.Operators.Add(new Operator(
                operatorId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId: $"sub-{operatorId.Value:N}"));
            // `25-170`: the Operator role's own seat status, not the removed account-level
            // Operator.HoldsSeat.
            db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId, HoldsSeat = holdsSeat });
            ids.Add(operatorId);
        }

        await db.SaveChangesAsync();
        return ids;
    }

    /// <summary>This test's own subject is the seat-count race, not authorization - a permission
    /// checker that always says yes keeps every concurrent call focused on exactly one thing.</summary>
    private sealed class AlwaysAllowPermissionChecker : IPermissionChecker
    {
        public Task<bool> HasPermissionAsync(OperatorId operatorId, SiteId siteId, Permission permission, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<string>> GetPermissionsAsync(OperatorId operatorId, SiteId siteId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        // `23-26`: not exercised by this class's own seat-count race - never called here, so an
        // unreachable-but-honest value rather than a real count this test has no seeded roles to answer.
        public Task<int> CountNonRemovedHoldersAsync(SiteId siteId, Permission permission, CancellationToken cancellationToken) =>
            Task.FromResult(1);
    }
}
