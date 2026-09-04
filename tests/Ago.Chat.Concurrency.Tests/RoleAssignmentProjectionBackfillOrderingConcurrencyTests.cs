using System.Text.Json;
using Ago.Chat.Application.UseCases.RemoveOperator;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Backfill;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>
/// `22-16`'s own report, requirement 3: "a backfill that emits many events at once must not be able to
/// land a stale snapshot after a fresh one for the same subject." Before this type existed, no two
/// publishers of <c>RoleAssignmentsChanged</c> could ever race for the same operator - each of the
/// other three's own precondition already serialised them (you cannot remove an operator who has not
/// yet redeemed their invite). <see cref="RoleAssignmentProjectionBackfill"/> is the first one that can:
/// its candidate list is built from a plain, unlocked read, so a real, concurrent
/// <see cref="RemoveOperatorHandler"/> call for the exact same operator is a real possibility during a
/// live run. This races them directly, against a real Postgres.
///
/// <para>This is also the test that separated the two claims <see cref="RoleAssignmentProjectionBackfill"/>'s
/// own remarks now make instead of one: a mutation removing its <c>FOR UPDATE</c> lock (and widening
/// the read-to-commit gap to 200ms to give a real race something to land in) still passed every
/// assertion below - the ordering property this test checks does not depend on the lock at all, only
/// on every row one run publishes sharing a single, frozen, early timestamp. What the lock actually
/// buys - not asserted here, see <see cref="Integration.Tests.RoleAssignmentProjectionBackfillTests"/>'s
/// own removed-operator test instead - is not staging a grant at all for an operator already known
/// removed at publish time.</para>
/// </summary>
[Collection(ConcurrencyCollection.Name)]
public sealed class RoleAssignmentProjectionBackfillOrderingConcurrencyTests(ConcurrencyTestFixture fixture, ITestOutputHelper output)
{
    /// <summary>
    /// Fifteen independent trials, each with fresh ids, racing <see cref="RoleAssignmentProjectionBackfill.PublishOneAsync"/>
    /// against a real <see cref="RemoveOperatorHandler"/> call for the identical operator, released from
    /// the same starting gate `OperatorInviteSeatLimitConcurrencyTests` already established this
    /// project's shape for. Both legal outcomes are asserted, not just one, because which one happens on
    /// a given trial depends on which side's own SQL statement reaches Postgres first - this test does
    /// not control that, and should not: it proves both interleavings resolve correctly, which is the
    /// actual claim. The win/loss split is printed rather than asserted on directly: the "backfill
    /// loses the race" branch is naturally rare (observed close to 1 in 15 trials against a real
    /// Postgres, since RemoveOperatorHandler's own several preceding steps give the backfill's much
    /// shorter path to its own row lock a head start most of the time), and requiring it to occur
    /// within a fixed, small trial count would make this test flake on entirely correct code roughly
    /// one run in three. `RoleAssignmentProjectionBackfillTests.APreviouslyRemovedOperator_...`
    /// (Integration.Tests) proves that exact branch deterministically instead, with no race involved.
    /// </summary>
    [Fact]
    public async Task ConcurrentRemoval_RacingTheBackfillForTheSameOperator_NeverLandsAStaleGrantAfterTheRevoke()
    {
        var backfillWon = 0;
        var backfillLost = 0;

        for (var trial = 0; trial < 15; trial++)
        {
            if (await RunOneTrialAsync(trial))
            {
                backfillWon++;
            }
            else
            {
                backfillLost++;
            }
        }

        output.WriteLine($"backfill won the race for the operator's own row {backfillWon}/15 time(s), lost it {backfillLost}/15 time(s) - both branches asserted correct above whichever occurred.");
    }

    /// <summary>Returns whether the backfill won the race for the operator's own row this trial
    /// (published a grant) or lost it (correctly published nothing).</summary>
    private async Task<bool> RunOneTrialAsync(int trial)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var requestedById = new OperatorId(Guid.NewGuid());
        var subjectId = $"sub-{trial}-{Guid.NewGuid():N}";
        var roleId = Guid.NewGuid();

        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            seed.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Offline, capacity: 5, subjectId));
            seed.Operators.Add(new Operator(requestedById, siteId, OperatorStatus.Offline, capacity: 5, $"admin-{trial}"));
            seed.Roles.Add(new RoleRecord
            {
                Id = roleId,
                SiteId = siteId,
                Name = "Admin",
                Permissions = [Permission.SiteManageOperators.Value, Permission.CalendarConfigure.Value],
            });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = requestedById, RoleId = roleId });
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        // The frozen "run start" timestamp RoleAssignmentProjectionBackfill.RunAsync would capture
        // once, before touching any candidate - read here, before either task starts, for the
        // identical reason. RemoveOperatorHandler below is given a real, live clock instead: this is
        // what makes the ordering assertion below a genuine claim about the production design (an
        // early, frozen backfill timestamp against whatever a real concurrent removal's own clock
        // reads at its own execution time) rather than a rigged one - if it were rigged to always sort
        // after, this test would pass even with no row lock in RoleAssignmentProjectionBackfill at
        // all, which is exactly the test that would prove nothing.
        var backfillRunStartedAt = DateTimeOffset.UtcNow;

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var backfillTask = Task.Run(async () =>
        {
            await gate.Task;
            await using var db = fixture.CreateDbContext();
            var backfill = new RoleAssignmentProjectionBackfill(db, new UuidV7Generator(), new FixedClock(backfillRunStartedAt));
            return await backfill.PublishOneAsync(operatorId, backfillRunStartedAt, CancellationToken.None);
        });

        var removeTask = Task.Run(async () =>
        {
            await gate.Task;
            await using var db = fixture.CreateDbContext();
            var handler = new RemoveOperatorHandler(
                new OperatorRepository(db), new PermissionChecker(db), new EfOutboxWriter<AgoChatDbContext>(db),
                new UuidV7Generator(), new RealClock());
            return await handler.HandleAsync(new RemoveOperator(requestedById, siteId, operatorId), CancellationToken.None);
        });

        gate.SetResult();
        await Task.WhenAll(backfillTask, removeTask);

        var backfillResult = await backfillTask;
        var removeResult = await removeTask;

        Assert.True(removeResult.IsSuccess, $"trial {trial}: RemoveOperatorHandler must succeed regardless of which side won the row lock.");

        await using var verify = fixture.CreateDbContext();
        var rows = await verify.Set<OutboxMessage>()
            .Where(o => o.Type == nameof(RoleAssignmentsChanged) && o.PartitionKey == subjectId)
            .OrderBy(o => o.OccurredAt)
            .ToListAsync(CancellationToken.None);

        if (backfillResult is null)
        {
            // The backfill lost the race for the operator's own row (removal's commit, or its own
            // row lock, landed first), re-checked removed_at, correctly saw the removal had already
            // happened, and published nothing - RoleAssignmentProjectionBackfill's own "re-checked
            // under the lock" branch. The removal's own event is the only, correct fact.
            var only = Assert.Single(rows);
            var contract = JsonSerializer.Deserialize<RoleAssignmentsChanged>(only.Payload)!;
            Assert.Empty(contract.Permissions);
            return false;
        }
        else
        {
            // The backfill won the race and published a grant regardless: both events exist, and this
            // is the property this test exists to prove - the backfill's grant sorts strictly before
            // the removal's own revoke by OccurredAt, the column Ago.Chat.Worker's own OutboxDispatcher
            // dispatches in order of, which is what makes the revoke the one that wins at the consumer
            // (an unconditional full replace) regardless of which side's transaction actually committed
            // to Postgres first. A stale grant landing after a fresh revoke would show up here as the
            // wrong ordering.
            Assert.Equal(2, rows.Count);
            var grantContract = JsonSerializer.Deserialize<RoleAssignmentsChanged>(rows[0].Payload)!;
            var revokeContract = JsonSerializer.Deserialize<RoleAssignmentsChanged>(rows[1].Payload)!;
            Assert.NotEmpty(grantContract.Permissions);
            Assert.Empty(revokeContract.Permissions);
            Assert.True(
                rows[0].OccurredAt < rows[1].OccurredAt,
                $"trial {trial}: the backfill's own grant ({rows[0].OccurredAt:o}) must dispatch-order "
                + $"before the removal's revoke ({rows[1].OccurredAt:o}).");
            return true;
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    /// <summary>What `RemoveOperatorHandler` gets in production (`Ago.Platform.Hosting.SystemClock`
    /// would do the identical thing) - a live read, not a value this test chose in advance.</summary>
    private sealed class RealClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
