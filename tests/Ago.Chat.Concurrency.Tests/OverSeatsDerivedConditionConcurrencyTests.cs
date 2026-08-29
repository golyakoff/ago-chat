using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetSeatAssignmentSummary;
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
/// is.</b> A downgrade writes <c>sites.seat_limit</c>; a seat toggle writes one <c>operators</c> row -
/// two independent rows, never contended against each other, so there is no race to serialize the way
/// two redemptions racing for the same seat need `SELECT ... FOR UPDATE` to serialize. What this item's
/// own Scope actually calls a derived, read-time condition needs proven is narrower and just as real:
/// that <see cref="GetSeatAssignmentSummaryHandler"/>'s own read - one query against `operators`, one
/// against `sites`, no shared lock between them - never reports a torn or stale combination once both
/// writes have actually committed, and that a storm of concurrent seat toggles racing
/// <see cref="ToggleOperatorSeatHandler"/>'s own capacity guard against a seat_limit that changes mid-storm
/// neither deadlocks nor lets the held-seat count drift from what the database actually holds.</para>
/// </summary>
[Collection(ConcurrencyCollection.Name)]
public sealed class OverSeatsDerivedConditionConcurrencyTests(ConcurrencyTestFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task ADowngrade_RacingConcurrentSeatToggles_LeavesTheDerivedReadConsistentWithWhatWasActuallyWritten()
    {
        const int initialSeatLimit = 10;
        const int downgradedSeatLimit = 3;
        const int holdingOperators = 6; // already over what the downgrade will allow
        const int seatlessOperators = 6; // each concurrently trying to toggle on

        var siteId = await SeedSiteAsync(initialSeatLimit);
        var holding = await SeedOperatorsAsync(siteId, holdingOperators, holdsSeat: true);
        var seatless = await SeedOperatorsAsync(siteId, seatlessOperators, holdsSeat: false);

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
            var handler = new ToggleOperatorSeatHandler(new OperatorRepository(db), new SiteRepository(db), new AlwaysAllowPermissionChecker());
            return await handler.HandleAsync(
                new ToggleOperatorSeat(new OperatorId(Guid.NewGuid()), siteId, operatorId, true), CancellationToken.None);
        })).ToList();

        gate.SetResult();
        await downgradeTask;
        var toggleOutcomes = await Task.WhenAll(toggleTasks);

        var toggledOn = toggleOutcomes.Count(r => r.IsSuccess);
        output.WriteLine($"toggledOn={toggledOn} of {seatlessOperators}; downgraded seat_limit={downgradedSeatLimit}");

        // The ground truth, read independently of the handler under test - what the database actually
        // holds after every write above has committed.
        await using var verify = fixture.CreateDbContext();
        var actualHeldSeats = await verify.Operators.AsNoTracking()
            .CountAsync(o => o.SiteId == siteId && o.HoldsSeat && o.RemovedAt == null);
        var actualSeatLimit = (await verify.Sites.AsNoTracking().SingleAsync(s => s.Id == siteId)).SeatLimit;
        Assert.Equal(downgradedSeatLimit, actualSeatLimit);
        Assert.Equal(holdingOperators + toggledOn, actualHeldSeats);

        // The derived read itself, against the same real data - must match the ground truth exactly,
        // not merely "close" or "eventually" - and since six operators already held a seat before the
        // downgrade to three, over-seats is true no matter how many of the racing toggles happened to
        // land before or after it.
        await using var summaryDb = fixture.CreateDbContext();
        var summaryHandler = new GetSeatAssignmentSummaryHandler(
            new OperatorRepository(summaryDb), new SiteRepository(summaryDb), new AlwaysAllowPermissionChecker());
        var summary = await summaryHandler.HandleAsync(
            new GetSeatAssignmentSummary(new OperatorId(Guid.NewGuid()), siteId), CancellationToken.None);

        Assert.True(summary.IsSuccess);
        Assert.Equal(actualHeldSeats, summary.Value.HeldSeats);
        Assert.Equal(actualSeatLimit, summary.Value.SeatLimit);
        Assert.True(summary.Value.OverSeats);
        Assert.True(summary.Value.HeldSeats > summary.Value.SeatLimit);
    }

    private async Task<SiteId> SeedSiteAsync(int seatLimit)
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [], seatLimit: seatLimit));
        await db.SaveChangesAsync();
        return siteId;
    }

    private async Task<List<OperatorId>> SeedOperatorsAsync(SiteId siteId, int count, bool holdsSeat)
    {
        var ids = new List<OperatorId>();
        await using var db = fixture.CreateDbContext();
        for (var i = 0; i < count; i++)
        {
            var operatorId = new OperatorId(Guid.NewGuid());
            db.Operators.Add(new Operator(
                operatorId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId: $"sub-{operatorId.Value:N}", holdsSeat: holdsSeat));
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
    }
}
