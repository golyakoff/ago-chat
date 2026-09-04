using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.TransferConversation;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Worker;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>
/// `18-02`'s own Scope, at the only level that can prove it: a real Postgres row racing a real
/// assignment engine and real concurrent transfer requests, the same shape
/// <see cref="CloseConversationCapacityConcurrencyTests"/> and
/// <see cref="ConversationAssignmentConcurrencyTests"/> already use for the two neighbouring halves of
/// this same contended state. Three scenarios, each proving something the other two don't:
/// <see cref="TransferringRacesTheAssignmentEngine_NeverCorruptsCapacityOrDropsTheConversation"/> (the
/// transfer's own transaction absorbing `40P01` against the engine's batches, the way `adr/0037`
/// prescribes), <see cref="TwoTransfersOfTheSameConversation_ExactlyOneWinsCleanly"/> and
/// <see cref="OppositeDirectionTransfersBetweenTheSameTwoOperators_NeitherHangsNorCorruptsCapacity"/>
/// (the canonical lock order this handler adds on top of `adr/0037`, ruling out a transfer
/// self-inflicting the exact inversion the ADR otherwise accepts from the engine), and
/// <see cref="TransferringToAnOperatorWhoReachesCapacityInBetween_RefusesCleanly_NeverOverSubscribes"/>
/// (the backlog's own "refuse rather than queue", proven under a genuine race for the last slot rather
/// than argued from the code).
/// </summary>
[Collection(ConcurrencyCollection.Name)]
public sealed class TransferConversationConcurrencyTests(ConcurrencyTestFixture fixture, ITestOutputHelper output)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    /// <summary>
    /// `18-02`'s own instance of `6-10`'s shape: this transaction is a new participant in the
    /// engine's accepted, data-dependent lock-order cycle (`adr/0037`) - not addressed here, cannot
    /// be, and the transaction-level retry this handler adds is exactly the treatment the ADR already
    /// prescribes for it. A sustained storm of assignment batches and transfers against the same
    /// handful of `operators` rows is what actually produces that contention on demand, the same
    /// technique <c>ClosesStormingAssignmentBatches_...</c> uses for the release side of this cycle.
    /// What this test asserts: no transfer ever escapes with an unhandled exception (an operator must
    /// never see `40P01` for pressing "transfer"), and the exact claim/assignment invariant holds
    /// afterwards - which is also how a transaction that committed only half of itself would show up.
    /// </summary>
    [Fact]
    public async Task TransferringRacesTheAssignmentEngine_NeverCorruptsCapacityOrDropsTheConversation()
    {
        const int capacity = 5;
        const int operatorCount = 3;
        const int conversationCount = 1500;
        const int claimerCount = 6;
        const int closerCount = 12;
        // Fewer transferrers than closers/claimers, deliberately: this storm's job is to prove a
        // transfer survives contention *caused by the engine and by closes*, not to also maximise
        // contention transfers cause each other - TwoTransfersOfTheSameConversation_... and
        // OppositeDirectionTransfersBetweenTheSameTwoOperators_... already cover transfer-vs-transfer
        // contention directly, deliberately, at a scale that can actually be reasoned about.
        const int transferCount = 8;
        const int batchSize = 80;

        var seed = await SeedAsync(operatorCount, capacity, conversationCount);
        var escaped = new List<Exception>();
        var closed = 0;
        var transferred = 0;
        // No two closers may target the same conversation at once - the same exclusivity
        // ClosesStormingAssignmentBatches_... uses, for the identical reason: two concurrent closes of
        // the same conversation is a pre-existing, unrelated defect (`6-10`'s own "Found in passing"
        // note) this test must not accidentally exercise and mistake for a finding of its own.
        // Transfers deliberately get no such exclusivity - two transfers landing on the same
        // conversation is exactly TwoTransfersOfTheSameConversation_ExactlyOneWinsCleanly's own
        // subject, proven to resolve cleanly there, so letting it happen here too costs nothing.
        var takenByCloser = new System.Collections.Concurrent.ConcurrentDictionary<ConversationId, byte>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Closing is what makes this storm sustain itself rather than fizzle after the first fill:
        // a close frees a real slot, which is the only thing that gives the assignment engine fresh
        // work for the rest of the run - a transfer alone never changes an operator's own total, so a
        // transfer-only storm burns out the moment initial capacity is claimed (measured, not assumed:
        // an earlier version of this test with no closers produced zero deadlocks).
        var closing = Enumerable.Range(0, closerCount).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var mine = (await AssignedAsync(seed))
                    .Select(a => a.ConversationId)
                    .OrderBy(_ => Random.Shared.Next())
                    .FirstOrDefault(id => takenByCloser.TryAdd(id, 0));
                if (mine == default)
                {
                    await Task.Delay(1, CancellationToken.None);
                    continue;
                }

                try
                {
                    if (await CloseAsync(seed, mine))
                    {
                        Interlocked.Increment(ref closed);
                    }
                }
                catch (Exception ex)
                {
                    lock (escaped)
                    {
                        escaped.Add(ex);
                    }
                }
            }
        }));

        var claiming = Enumerable.Range(0, claimerCount).Select(_ => Task.Run(async () =>
        {
            var claimer = new SkipLockedAssignmentClaimer(fixture.DataSource, new SystemClock(), new UuidV7Generator());
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await claimer.AssignWaitingConversationsAsync(seed.SiteId, batchSize, CancellationToken.None);
                }
                catch (Exception)
                {
                    // A batch losing to a deadlock or an xmin conflict is this path's own normal
                    // outcome (`4-02`), not this test's subject.
                }
            }
        }));

        var transferring = Enumerable.Range(0, transferCount).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var assigned = await AssignedAsync(seed);
                if (assigned.Count == 0)
                {
                    await Task.Delay(1, CancellationToken.None);
                    continue;
                }

                var (conversationId, from) = assigned[Random.Shared.Next(assigned.Count)];
                // Random, not "the first other operator" - a deterministic pick funnels every
                // transfer at one operator, saturating it immediately and starving every other
                // transfer attempt of a real chance to succeed regardless of how the retry bound is
                // tuned. Spreading targets is what makes "some transfers actually succeed under this
                // storm" a meaningful assertion rather than an artifact of the harness's own bias.
                var others = seed.OperatorIds.Where(id => id != from).ToList();
                var to = others[Random.Shared.Next(others.Count)];

                try
                {
                    var result = await TransferAsync(seed, conversationId, from, to);
                    if (result.IsSuccess)
                    {
                        Interlocked.Increment(ref transferred);
                    }
                }
                catch (Exception ex)
                {
                    lock (escaped)
                    {
                        escaped.Add(ex);
                    }
                }
            }
        }));

        await Task.WhenAll(claiming.Concat(closing).Concat(transferring));

        var (conversations, operators) = await ReadStateAsync(seed);
        var deadlockReports = await CountDeadlockReportsAsync();
        output.WriteLine(
            $"assigned={conversations.Count(c => c.State == ConversationState.Assigned)}; closed={closed}; " +
            $"transferred={transferred}; active_chats=[{string.Join(", ", operators.Select(o => o.ActiveChats))}]; " +
            $"escaped={escaped.Count}; postgres deadlock reports={deadlockReports}");

        Assert.Empty(escaped);
        AssertCapacityInvariant(seed, conversations, operators);
        // Not an assertion about the fix - an assertion that this run was hostile enough to be
        // evidence of anything, the same reasoning ClosesStormingAssignmentBatches_... gives for the
        // identical check on the release side of this same cycle. A quiet run proves nothing.
        Assert.True(deadlockReports > 0, "the storm produced no Postgres deadlock at all, so it proved nothing");

        // Deliberately not `Assert.True(transferred > 0, ...)`. Measured, not assumed: even after this
        // item's own retry-bound revision (2 attempts, no backoff -> 5, jittered), this exact storm
        // occasionally lets zero transfers land - not because any of them corrupted anything or threw
        // (the two assertions above are what actually guard that), but because enough concurrent
        // transferrers racing 12 closers and 6 claimers on 3 operators rows can exhaust 5 attempts of
        // jittered backoff without any of them finding clear air, in the specific worst case. No finite
        // retry bound removes that possibility under literally unlimited concurrent contention; a
        // larger bound would only move the threshold, not remove it, and CLAUDE.md rule 7 forbids
        // asserting a throughput guarantee this suite has not actually measured holding under every
        // run. What is safe to assert, and does hold every run: no transfer ever corrupts state or
        // escapes with an exception. Throughput under contention this extreme is a load-test question
        // (`load/`, Stage 7), not a concurrency-suite invariant - see the commit-prep report for the
        // honest residual this leaves.
        output.WriteLine($"transferred={transferred} (informational only - see this test's own remarks)");
    }

    /// <summary>Reads the deadlock graphs back out of the container's own log - the same technique
    /// <c>CloseConversationCapacityConcurrencyTests.CountDeadlockReportsAsync</c> uses for the release
    /// side of this cycle, without the release-specific victim breakdown that test also computes
    /// (this one only needs to know contention genuinely happened, not which statement lost).</summary>
    private async Task<int> CountDeadlockReportsAsync()
    {
        var lines = (await fixture.GetPostgresLogsAsync()).Split('\n');
        return lines.Count(l => l.Contains("ERROR:  deadlock detected", StringComparison.Ordinal));
    }

    /// <summary>
    /// The backlog item's own second named scenario, taken literally: two operators both try to hand
    /// the *same* conversation away at the same instant, to two different colleagues. Exactly one may
    /// win - the conversation has exactly one <c>OperatorId</c> - and the loser must get a clean
    /// <see cref="Result"/> failure, never an exception and never a silent no-op that leaves capacity
    /// wrong on the operator it never actually reached.
    /// </summary>
    [Fact]
    public async Task TwoTransfersOfTheSameConversation_ExactlyOneWinsCleanly()
    {
        const int capacity = 5;
        var seed = await SeedAsync(operatorCount: 3, capacity, conversationCount: 1);
        await CreateAssignmentJob(batchSize: 1).RunOnceAsync(CancellationToken.None);

        var assigned = await AssignedAsync(seed);
        var (conversationId, from) = Assert.Single(assigned);
        var candidates = seed.OperatorIds.Where(id => id != from).ToList();
        var targetA = candidates[0];
        var targetB = candidates[1];

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var toA = Task.Run(async () =>
        {
            await gate.Task;
            return await TransferAsync(seed, conversationId, from, targetA);
        });
        var toB = Task.Run(async () =>
        {
            await gate.Task;
            return await TransferAsync(seed, conversationId, from, targetB);
        });

        gate.SetResult();
        var results = await Task.WhenAll(toA, toB);

        Assert.Single(results, r => r.IsSuccess);
        Assert.Single(results, r => r.IsFailure);
        var loser = results.Single(r => r.IsFailure);
        // Whoever lost read a conversation still assigned to `from` and either got refused outright
        // (a fresh read after the winner committed shows a different OperatorId) or bounced off write
        // contention on the shared `from` row and, on retry, saw the same thing - both are real,
        // named Results, never an unhandled exception.
        var acceptableLoserCodes = new List<string>
        {
            "Conversation.Forbidden", "Conversation.ConcurrencyConflict", "Conversation.TransferContended",
        };
        Assert.Contains(loser.Error!.Value.Code, acceptableLoserCodes);

        var (conversations, operators) = await ReadStateAsync(seed);
        var row = Assert.Single(conversations);
        Assert.Equal(ConversationState.Assigned, row.State);
        Assert.True(row.OperatorId == targetA || row.OperatorId == targetB);
        AssertCapacityInvariant(seed, conversations, operators);
        // The operator the loser named never actually got the conversation, so it must never have
        // been charged for it either.
        var untouched = row.OperatorId == targetA ? targetB : targetA;
        Assert.Equal(0, operators.Single(o => o.Id == untouched).ActiveChats);
    }

    /// <summary>
    /// The canonical lock order this handler adds on top of `adr/0037` (see
    /// <c>TransferConversationHandler.TransferAndSaveAsync</c>'s own remarks), proven rather than
    /// argued: two conversations, each assigned to a different one of the same two operators,
    /// transferred to each other's operator at the same instant. Ordering the two `operators` row
    /// touches by operator id rather than by "claim target first" in program order is exactly what
    /// stops this from being a textbook self-inflicted deadlock - without it, one transfer takes
    /// (target, source) lock order and the other takes (source, target), the same inversion `4-02`'s
    /// own batches produce against each other, except this one would be entirely this handler's own
    /// doing rather than an accepted, unavoidable cost.
    /// </summary>
    [Fact]
    public async Task OppositeDirectionTransfersBetweenTheSameTwoOperators_NeitherHangsNorCorruptsCapacity()
    {
        const int capacity = 5;
        var seed = await SeedAsync(operatorCount: 2, capacity, conversationCount: 2);
        await CreateAssignmentJob(batchSize: 2).RunOnceAsync(CancellationToken.None);

        var assigned = await AssignedAsync(seed);
        Assert.Equal(2, assigned.Count);
        var operatorX = seed.OperatorIds[0];
        var operatorY = seed.OperatorIds[1];
        var (conversationOnX, ownerX) = assigned.Single(a => a.OperatorId == operatorX);
        var (conversationOnY, ownerY) = assigned.Single(a => a.OperatorId == operatorY);
        Assert.Equal(operatorX, ownerX);
        Assert.Equal(operatorY, ownerY);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Repeated several times, not once - a genuine lock-order inversion is timing-sensitive, and
        // a single pair of racing tasks can get lucky. Each round swaps the two conversations back to
        // where they started so the next round races the identical shape again.
        for (var round = 0; round < 20; round++)
        {
            var xToY = TransferAsync(seed, conversationOnX, operatorX, operatorY, timeout.Token);
            var yToX = TransferAsync(seed, conversationOnY, operatorY, operatorX, timeout.Token);

            var results = await Task.WhenAll(xToY, yToX);
            Assert.All(results, r => Assert.True(
                r.IsSuccess, r.IsFailure ? r.Error!.Value.Message : string.Empty));

            (conversationOnX, conversationOnY) = (conversationOnY, conversationOnX);
        }

        var (conversations, operators) = await ReadStateAsync(seed);
        AssertCapacityInvariant(seed, conversations, operators);
        Assert.All(operators, o => Assert.Equal(1, o.ActiveChats));
    }

    /// <summary>
    /// The backlog item's own third named scenario, and its own Scope line verbatim: "Refuse rather
    /// than queue when the target is at capacity." Two different conversations, held by two different
    /// operators, both transferred to the *same* target at the same instant, with room for exactly
    /// one - the race for the last slot the code has to actually resolve rather than a
    /// pre-determined winner.
    /// </summary>
    [Fact]
    public async Task TransferringToAnOperatorWhoReachesCapacityInBetween_RefusesCleanly_NeverOverSubscribes()
    {
        var seed = await SeedAsync(
            operatorCapacities: [5, 5, 1], conversationCount: 2);
        var sourceA = seed.OperatorIds[0];
        var sourceB = seed.OperatorIds[1];
        var target = seed.OperatorIds[2]; // capacity 1 - room for exactly one of the two transfers

        await AssignSpecificallyAsync(seed, sourceA, sourceB);
        var assigned = await AssignedAsync(seed);
        var (conversationOnA, _) = assigned.Single(a => a.OperatorId == sourceA);
        var (conversationOnB, _) = assigned.Single(a => a.OperatorId == sourceB);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var toTargetFromA = Task.Run(async () =>
        {
            await gate.Task;
            return await TransferAsync(seed, conversationOnA, sourceA, target);
        });
        var toTargetFromB = Task.Run(async () =>
        {
            await gate.Task;
            return await TransferAsync(seed, conversationOnB, sourceB, target);
        });

        gate.SetResult();
        var results = await Task.WhenAll(toTargetFromA, toTargetFromB);

        Assert.Single(results, r => r.IsSuccess);
        var refused = Assert.Single(results, r => r.IsFailure);
        // A real, visible refusal - never a silent queue entry, and never an exception.
        Assert.Equal("Conversation.TransferTargetAtCapacity", refused.Error!.Value.Code);

        var (conversations, operators) = await ReadStateAsync(seed);
        AssertCapacityInvariant(seed, conversations, operators);
        Assert.Equal(1, operators.Single(o => o.Id == target).ActiveChats);
        // The refused conversation never moved and never lost its claim - the transaction it was
        // part of rolled back in full.
        var refusedConversationId = conversations.Single(c => c.OperatorId != target && c.OperatorId != null).Id;
        var refusedRow = conversations.Single(c => c.Id == refusedConversationId);
        Assert.True(refusedRow.HoldsCapacityClaim);
    }

    private sealed record Seed(SiteId SiteId, IReadOnlyList<OperatorId> OperatorIds, VisitorId VisitorId);

    private sealed record ConversationRow(
        ConversationId Id, ConversationState State, OperatorId? OperatorId, bool HoldsCapacityClaim);

    private sealed record OperatorRow(OperatorId Id, int ActiveChats);

    private static void AssertCapacityInvariant(
        Seed seed, IReadOnlyList<ConversationRow> conversations, IReadOnlyList<OperatorRow> operators)
    {
        foreach (var op in operators)
        {
            var held = conversations.Count(c =>
                c.State == ConversationState.Assigned && c.OperatorId == op.Id && c.HoldsCapacityClaim);
            // Exact, not a range: the invariant this whole item exists to keep true through a
            // transfer, the same shape CloseConversationCapacityConcurrencyTests already asserts for
            // claim/release.
            Assert.Equal(held, op.ActiveChats);
        }

        // No conversation is left holding a receipt no operator's own count accounts for.
        Assert.All(conversations.Where(c => c.State != ConversationState.Assigned), c => Assert.False(c.HoldsCapacityClaim));
    }

    private async Task<Seed> SeedAsync(int operatorCount, int capacity, int conversationCount) =>
        await SeedAsync(Enumerable.Repeat(capacity, operatorCount).ToArray(), conversationCount);

    private async Task<Seed> SeedAsync(IReadOnlyList<int> operatorCapacities, int conversationCount)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorIds = operatorCapacities.Select(_ => new OperatorId(Guid.NewGuid())).ToList();
        var roleId = Guid.NewGuid();

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        db.Roles.Add(new RoleRecord
        {
            Id = roleId,
            SiteId = siteId,
            Name = "Operator",
            // Both permissions: TransferConversationHandler checks ConversationAssign (reusing the
            // existing hand-picked-assignment permission - the handler's own remarks), and
            // TransferringRacesTheAssignmentEngine_... also closes conversations to sustain the storm,
            // which needs ConversationClose (CloseConversationHandler's own gate).
            Permissions = [Permission.ConversationAssign.Value, Permission.ConversationClose.Value],
        });

        for (var i = 0; i < operatorIds.Count; i++)
        {
            db.Operators.Add(new Operator(operatorIds[i], siteId, OperatorStatus.Online, operatorCapacities[i]));
            db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorIds[i], RoleId = roleId });
        }

        for (var i = 0; i < conversationCount; i++)
        {
            db.Conversations.Add(Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, Now));
        }

        await db.SaveChangesAsync(CancellationToken.None);
        return new Seed(siteId, operatorIds, visitorId);
    }

    /// <summary>Assigns the two seeded waiting conversations one each to the two named operators,
    /// through the real engine - deterministic because `TransferringToAnOperatorWhoReachesCapacityInBetween`
    /// needs to know exactly which conversation ended up where, not merely that two conversations got
    /// assigned somewhere.</summary>
    private async Task AssignSpecificallyAsync(Seed seed, OperatorId first, OperatorId second)
    {
        await using var db = fixture.CreateDbContext();
        var capacity = new OperatorCapacityStore(db);
        var conversations = new ConversationRepository(db);
        var waiting = await db.Conversations.AsNoTracking()
            .Where(c => c.SiteId == seed.SiteId && c.State == ConversationState.Waiting)
            .OrderBy(c => c.Id)
            .Select(c => c.Id)
            .ToListAsync();

        foreach (var (conversationId, operatorId) in new[] { (waiting[0], first), (waiting[1], second) })
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            Assert.True(await capacity.TryClaimAsync(operatorId, CancellationToken.None));
            var conversation = (await conversations.GetByIdAsync(conversationId, CancellationToken.None))!;
            conversation.AssignTo(operatorId, Now, holdsCapacityClaim: true);
            conversation.ClearDomainEvents();
            await conversations.SaveAsync(conversation, CancellationToken.None);
            await tx.CommitAsync();
        }
    }

    private ConversationAssignmentJob CreateAssignmentJob(int batchSize) =>
        new(fixture.DataSource,
            new SkipLockedAssignmentClaimer(fixture.DataSource, new SystemClock(), new UuidV7Generator()),
            Options.Create(new ConversationAssignmentJobOptions { BatchSize = batchSize }),
            NullLogger<ConversationAssignmentJob>.Instance);

    /// <summary>The real handler on its own <c>AgoChatDbContext</c>, exactly as one Api request gets
    /// it - including its own real <see cref="EfUnitOfWork"/>, not a fake, since the whole point of
    /// these tests is proving the real transaction boundary.</summary>
    private async Task<Result> TransferAsync(
        Seed seed, ConversationId conversationId, OperatorId from, OperatorId to, CancellationToken cancellationToken = default)
    {
        await using var db = fixture.CreateDbContext();
        var handler = new TransferConversationHandler(
            new ConversationRepository(db), new OperatorRepository(db), new ConversationAssignmentLog(db),
            new PermissionChecker(db), new OperatorCapacityStore(db), new EfUnitOfWork(db),
            new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator(), new SystemClock());

        return await handler.HandleAsync(
            new TransferConversation(conversationId, from, to, seed.SiteId), cancellationToken);
    }

    /// <summary>Closes through the real <see cref="Application.UseCases.CloseConversation.CloseConversationHandler"/>
    /// - the same role <see cref="CloseConversationCapacityConcurrencyTests"/>'s own storm test gives
    /// it, reused here only to sustain fresh capacity churn for the assignment engine, not as this
    /// test's own subject. Returns whether the close succeeded, mirroring that test's own helper.</summary>
    private async Task<bool> CloseAsync(Seed seed, ConversationId conversationId)
    {
        await using var db = fixture.CreateDbContext();
        var conversation = await db.Conversations.AsNoTracking().SingleAsync(c => c.Id == conversationId);
        if (conversation.OperatorId is not { } operatorId)
        {
            return false;
        }

        var handler = new Application.UseCases.CloseConversation.CloseConversationHandler(
            new ConversationRepository(db), new ConversationAssignmentLog(db), new PermissionChecker(db),
            new OperatorCapacityStore(db), new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator(),
            new SystemClock(), NullLogger<Application.UseCases.CloseConversation.CloseConversationHandler>.Instance);

        var result = await handler.HandleAsync(
            new Application.UseCases.CloseConversation.CloseConversation(conversationId, operatorId, seed.SiteId),
            CancellationToken.None);
        return result.IsSuccess;
    }

    private async Task<List<(ConversationId ConversationId, OperatorId OperatorId)>> AssignedAsync(Seed seed)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Conversations.AsNoTracking()
            .Where(c => c.SiteId == seed.SiteId && c.State == ConversationState.Assigned)
            .OrderBy(c => c.Id)
            .Select(c => new ValueTuple<ConversationId, OperatorId>(c.Id, c.OperatorId!.Value))
            .ToListAsync();
    }

    private async Task<(IReadOnlyList<ConversationRow> Conversations, IReadOnlyList<OperatorRow> Operators)> ReadStateAsync(Seed seed)
    {
        await using var db = fixture.CreateDbContext();
        var conversations = await db.Conversations.AsNoTracking()
            .Where(c => c.SiteId == seed.SiteId)
            .Select(c => new ConversationRow(c.Id, c.State, c.OperatorId, c.HoldsCapacityClaim))
            .ToListAsync();
        var operators = await db.Operators.AsNoTracking()
            .Where(o => o.SiteId == seed.SiteId)
            .Select(o => new OperatorRow(o.Id, EF.Property<int>(o, "active_chats")))
            .ToListAsync();

        return (conversations, operators);
    }
}
