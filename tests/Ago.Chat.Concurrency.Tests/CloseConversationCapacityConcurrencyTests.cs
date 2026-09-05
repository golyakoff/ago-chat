using System.Collections.Concurrent;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.AssignConversation;
using Ago.Chat.Application.UseCases.CloseConversation;
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
/// `6-09`'s Done-when, at the only level that can prove it: the two halves of a capacity claim's
/// lifecycle running against one real Postgres row - <c>OperatorCapacityStore.TryClaimAsync</c> inside
/// <c>Ago.Chat.Worker</c>'s assignment tick, and <c>CloseConversationHandler</c>'s release inside an
/// <c>Ago.Chat.Api</c> request. Two processes in production, no shared lock, only the atomic
/// compare-and-set on <c>operators.active_chats</c> and the conversation row's own `xmin`.
///
/// <para><b>The invariant every test here asserts</b> is the one the whole item exists to restore:
/// <c>active_chats</c> equals the number of conversations currently <c>Assigned</c> to that operator
/// <em>that hold a capacity claim</em> - exactly, not within a range. Neighbouring
/// <c>ConversationAssignmentConcurrencyTests</c> already proves the claim half never over-shoots
/// capacity; the never-tested half until now was that the number ever comes back down.</para>
/// </summary>
[Collection(ConcurrencyCollection.Name)]
public sealed class CloseConversationCapacityConcurrencyTests(ConcurrencyTestFixture fixture, ITestOutputHelper output)
{
    // Real time, not a fixed date - the `messages` table is partitioned by month (see
    // MarkConversationReadConcurrencyTests' own note), and the racing writer below inserts a message.
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    /// <summary>
    /// The bug, reproduced deterministically end to end and against the real assignment engine: fill
    /// an operator's capacity through <c>ConversationAssignmentJob</c>, close every one of those
    /// conversations through the real handler, and the operator must be assignable again. Against
    /// pre-`6-09` code this fails on the very first assertion - <c>active_chats</c> stays pinned at
    /// <c>capacity</c> forever and the remaining conversations never leave `Waiting`, which is
    /// precisely what `7-04`'s <c>assignment-contention</c> run measured as a 51/150 plateau.
    /// </summary>
    [Fact]
    public async Task ClosingEveryAssignedConversation_ReturnsTheCapacity_AndTheQueueDrainsPastTheFirstCapacitysWorth()
    {
        const int capacity = 4;
        const int conversationCount = 10; // more than one capacity's worth, on purpose
        var seed = await SeedAsync(operatorCount: 1, capacity, conversationCount);
        var job = CreateAssignmentJob(conversationCount);

        await job.RunOnceAsync(CancellationToken.None);
        var firstWave = await AssignedIdsAsync(seed);
        Assert.Equal(capacity, firstWave.Count);
        Assert.Equal(capacity, await ActiveChatsAsync(seed.OperatorIds[0]));

        foreach (var conversationId in firstWave)
        {
            Assert.True(await CloseAsync(seed, conversationId));
        }

        // The single number this whole item is about.
        Assert.Equal(0, await ActiveChatsAsync(seed.OperatorIds[0]));

        // ...and the consequence that actually matters to a running site: the queue moves again.
        await job.RunOnceAsync(CancellationToken.None);
        var secondWave = await AssignedIdsAsync(seed);
        Assert.Equal(capacity, secondWave.Count);
        Assert.Empty(secondWave.Intersect(firstWave));
        Assert.Equal(capacity, await ActiveChatsAsync(seed.OperatorIds[0]));
    }

    /// <summary>
    /// Closes racing assignments, both for real: several assignment ticks (standing in for several
    /// `Worker` replicas) and several closes released together onto the thread pool, repeated over
    /// many rounds. Neither side takes a lock the other respects - the claim is one atomic
    /// compare-and-set, the release another, and the conversation's own `xmin` arbitrates the state
    /// change - so if the accounting were racy at all this is where it would drift.
    /// </summary>
    [Fact]
    public async Task ClosesRacingAssignments_NeverCorruptTheCount()
    {
        const int capacity = 5;
        const int operatorCount = 2;
        const int conversationCount = 60;
        const int rounds = 12;
        var seed = await SeedAsync(operatorCount, capacity, conversationCount);
        var jobs = Enumerable.Range(0, 3).Select(_ => CreateAssignmentJob(batchSize: 20)).ToList();
        var closed = 0;

        for (var round = 0; round < rounds; round++)
        {
            var assigned = await AssignedIdsAsync(seed);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var assigning = jobs.Select(job => Task.Run(async () =>
            {
                await gate.Task;
                await job.RunOnceAsync(CancellationToken.None);
            }));
            // Half the currently-assigned conversations close at the same instant the next tick is
            // claiming - the interleaving that matters, since a claim and a release land on the same
            // operators row.
            var closing = assigned.Take(assigned.Count / 2 + 1).Select(id => Task.Run(async () =>
            {
                await gate.Task;
                if (await CloseAsync(seed, id))
                {
                    Interlocked.Increment(ref closed);
                }
            }));

            gate.SetResult();
            await Task.WhenAll(assigning.Concat(closing));
        }

        await using var verify = fixture.CreateDbContext();
        var conversations = await verify.Conversations.AsNoTracking()
            .Where(c => c.SiteId == seed.SiteId)
            .ToListAsync();
        var operators = await verify.Operators.AsNoTracking()
            .Where(o => o.SiteId == seed.SiteId)
            .Select(o => new { o.Id, ActiveChats = EF.Property<int>(o, "active_chats") })
            .ToListAsync();

        output.WriteLine(
            $"rounds={rounds}; conversations={conversationCount}; closed={closed}; " +
            $"assigned={conversations.Count(c => c.State == ConversationState.Assigned)}; " +
            $"waiting={conversations.Count(c => c.State == ConversationState.Waiting)}; " +
            $"active_chats=[{string.Join(", ", operators.Select(o => o.ActiveChats))}]");

        // Enough closes actually happened for the assertions below to mean something - a run where
        // nothing closed would pass every invariant vacuously.
        Assert.True(closed > capacity * operatorCount, $"only {closed} conversations closed");

        foreach (var op in operators)
        {
            var held = conversations.Count(c =>
                c.State == ConversationState.Assigned && c.OperatorId == op.Id && c.HoldsCapacityClaim);
            // Exact, not a range: every claim taken is accounted for by a live assignment, and every
            // live engine-made assignment is accounted for by a slot.
            Assert.Equal(held, op.ActiveChats);
            Assert.InRange(op.ActiveChats, 0, capacity);
        }

        // No conversation is left holding a receipt it can never hand back - an orphaned claim is
        // exactly the leak this item is about, just moved one table over.
        Assert.All(conversations.Where(c => c.State != ConversationState.Assigned),
            c => Assert.False(c.HoldsCapacityClaim));
    }

    /// <summary>
    /// `6-08`'s retry-once, arranged rather than hoped for: a real, fully-committed concurrent write
    /// lands while the close is inside <c>SaveAsync</c>, so the first save provably loses on `xmin`
    /// and the handler reloads and closes again. The claim must be released exactly once across both
    /// attempts.
    ///
    /// <para>The operator deliberately holds <em>two</em> claims and only one conversation is closed.
    /// With a single claim, a double release would be invisible: <c>ReleaseAsync</c>'s floor at zero
    /// (<c>AND active_chats &gt; 0</c>) would silently absorb the second decrement and the test would
    /// pass for the wrong reason. Starting at two makes the difference between correct and
    /// double-released the difference between 1 and 0.</para>
    /// </summary>
    [Fact]
    public async Task CloseRetriedAfterAnXminConflict_ReleasesTheClaimExactlyOnce()
    {
        const int capacity = 4;
        var seed = await SeedAsync(operatorCount: 1, capacity, conversationCount: 2);
        var operatorId = seed.OperatorIds[0];
        await CreateAssignmentJob(batchSize: 2).RunOnceAsync(CancellationToken.None);

        var assigned = await AssignedIdsAsync(seed);
        Assert.Equal(2, assigned.Count);
        Assert.Equal(2, await ActiveChatsAsync(operatorId));

        var target = assigned[0];
        await using var db = fixture.CreateDbContext();
        var racing = new RacingConversationRepository(
            new ConversationRepository(db),
            maxInjections: 1,
            () => SendConcurrentVisitorMessageAsync(seed, target));
        var handler = new CloseConversationHandler(
            racing, new ConversationAssignmentLog(db), new PermissionChecker(db), new OperatorCapacityStore(db),
            new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator(), new SystemClock(),
            NullLogger<CloseConversationHandler>.Instance);

        var result = await handler.HandleAsync(
            new Application.UseCases.CloseConversation.CloseConversation(target, operatorId, seed.SiteId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : string.Empty);
        // Two saves: the first lost the race, the second went in against the fresh row.
        Assert.Equal(2, racing.SaveAttempts);
        // One release, not two. The second attempt reloaded a row whose receipt was still unspent
        // (the first attempt's save rolled back with it) and spent it once.
        Assert.Equal(1, await ActiveChatsAsync(operatorId));

        // The same close, replayed by a client that retried the whole request: rejected as already
        // closed, before any release is reached.
        Assert.False(await CloseAsync(seed, target));
        Assert.Equal(1, await ActiveChatsAsync(operatorId));

        await using var verify = fixture.CreateDbContext();
        var closedRow = await verify.Conversations.AsNoTracking().SingleAsync(c => c.Id == target);
        Assert.Equal(ConversationState.Closed, closedRow.State);
        Assert.False(closedRow.HoldsCapacityClaim);
    }

    /// <summary>
    /// The other half of the item's own open question, proven rather than argued: a hand-picked
    /// conversation (<c>AssignConversationHandler</c>'s path, which never calls <c>TryClaimAsync</c>)
    /// holds no claim, so closing it must decrement nothing - even while the same operator genuinely
    /// holds engine-made claims that a stray decrement would eat into.
    /// </summary>
    [Fact]
    public async Task ClosingAHandPickedConversation_DecrementsNothing()
    {
        const int capacity = 4;
        var seed = await SeedAsync(operatorCount: 1, capacity, conversationCount: 2);
        var operatorId = seed.OperatorIds[0];
        await CreateAssignmentJob(batchSize: 1).RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, await ActiveChatsAsync(operatorId));

        var handPicked = (await WaitingIdsAsync(seed))[0];
        await using (var db = fixture.CreateDbContext())
        {
            var repository = new ConversationRepository(db);
            var conversation = (await repository.GetByIdAsync(handPicked, CancellationToken.None))!;
            conversation.AssignTo(operatorId, Now); // no holdsCapacityClaim - the manual path's default
            conversation.ClearDomainEvents();
            await repository.SaveAsync(conversation, CancellationToken.None);
        }

        Assert.Equal(1, await ActiveChatsAsync(operatorId));
        Assert.True(await CloseAsync(seed, handPicked));
        Assert.Equal(1, await ActiveChatsAsync(operatorId));
    }

    /// <summary>
    /// `6-10`'s regression test, and the shape of contention that produced the CI failure this item
    /// exists for. <see cref="ClosesRacingAssignments_NeverCorruptTheCount"/> above runs in rounds -
    /// everyone starts together, everyone finishes, repeat - which proves the accounting but leaves
    /// gaps where nothing overlaps. This one is a sustained storm instead: assignment batches and
    /// closes run continuously against the same handful of <c>operators</c> rows, which is what a
    /// loaded CI runner produced by accident and a round-based test does not.
    ///
    /// <para><b>What broke.</b> A batch holds several <c>operators</c> row locks at once (one per
    /// operator it assigned to) for the rest of its batch, and two batches taking the same rows in a
    /// different order deadlock - known since `4-02`, caught per-tick, deliberately accepted. What
    /// `6-09` changed is who else is standing there: the close's <c>ReleaseAsync</c>, a *single-row*
    /// <c>UPDATE</c> in its own implicit transaction, which looks structurally incapable of
    /// deadlocking. It is not. Before it waits for the row's current updater it takes a heavyweight
    /// tuple lock on that row as its place in the queue, and a batch already holding a different
    /// operators row can then queue behind it - so Postgres's cycle runs *through* a statement that
    /// holds no row lock of its own, and it can pick that statement as the victim. The captured graph
    /// is in `6-10`'s backlog item.</para>
    ///
    /// <para><b>What this asserts.</b> Three things, and the third is what stops it passing vacuously:
    /// no close ever escapes with an exception (an operator must never see `40P01` for pressing
    /// "close"); the exact claim/assignment invariant still holds afterwards, which is also how an
    /// abandoned release would show up - the adapter's retry bound being too small reads here as
    /// <c>active_chats</c> exceeding the claims actually held; and Postgres really did detect
    /// deadlocks during the run, read back from the container's own log. A run with zero deadlock
    /// reports proved nothing and says so.</para>
    /// </summary>
    [Fact]
    public async Task ClosesStormingAssignmentBatches_NeverSurfaceADeadlockAndNeverCorruptTheCount()
    {
        const int capacity = 5;
        const int operatorCount = 3;
        const int conversationCount = 1500;
        const int claimerCount = 6;
        const int closerCount = 16;
        const int batchSize = 80;

        var seed = await SeedAsync(operatorCount, capacity, conversationCount);
        var escaped = new List<Exception>();
        // No two closers may target the same conversation at once. That is not the contention under
        // test - it is close-versus-close on one *conversation* row, whose losing side has its own
        // pre-existing behaviour - and letting it in here would drown the signal this test is for,
        // which is close-versus-assignment on one *operators* row.
        var taken = new ConcurrentDictionary<ConversationId, byte>();
        var closed = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        void Record(Exception ex)
        {
            lock (escaped)
            {
                escaped.Add(ex);
            }
        }

        // The claimer directly, not through ConversationAssignmentJob: the job catches its own
        // batch-level deadlock and moves on (`4-02`), which is correct in production and would hide
        // from this test whether the storm produced any contention at all.
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
                    // A batch losing - to a deadlock or to an `xmin` conflict - is this path's normal
                    // outcome and its own caller's business, not this test's subject.
                }
            }
        }));

        var closing = Enumerable.Range(0, closerCount).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var mine = (await AssignedIdsAsync(seed))
                    .OrderBy(_ => Random.Shared.Next())
                    .FirstOrDefault(id => taken.TryAdd(id, 0));
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
                    Record(ex);
                }
            }
        }));

        await Task.WhenAll(claiming.Concat(closing));

        await using var verify = fixture.CreateDbContext();
        var conversations = await verify.Conversations.AsNoTracking()
            .Where(c => c.SiteId == seed.SiteId)
            .ToListAsync();
        var operators = await verify.Operators.AsNoTracking()
            .Where(o => o.SiteId == seed.SiteId)
            .Select(o => new { o.Id, ActiveChats = EF.Property<int>(o, "active_chats") })
            .ToListAsync();

        var (deadlockReports, releaseVictims) = await CountDeadlockReportsAsync();
        output.WriteLine(
            $"closed={closed}; escaped={escaped.Count}; postgres deadlock reports={deadlockReports}, " +
            $"of which the close's release was the victim={releaseVictims}; " +
            $"active_chats=[{string.Join(", ", operators.Select(o => o.ActiveChats))}]");

        Assert.Empty(escaped);
        Assert.True(closed > capacity * operatorCount, $"only {closed} conversations closed");

        foreach (var op in operators)
        {
            var held = conversations.Count(c =>
                c.State == ConversationState.Assigned && c.OperatorId == op.Id && c.HoldsCapacityClaim);
            Assert.Equal(held, op.ActiveChats);
        }

        // Not an assertion about the fix - an assertion that the run was hostile enough to be
        // evidence of anything. The fixture's own 10 ms `deadlock_timeout` is what makes this
        // dependable rather than lucky.
        Assert.True(deadlockReports > 0, "the storm produced no Postgres deadlock at all, so it proved nothing");
    }

    /// <summary>Reads the deadlock graphs back out of the container's own log - `6-10`'s own scope
    /// note, that a run which fails and discards the server's explanation costs another full cycle to
    /// learn nothing. The second number is the one the item is about: how often the victim Postgres
    /// picked was the close's single-statement release rather than an assignment batch.</summary>
    private async Task<(int Reports, int ReleaseVictims)> CountDeadlockReportsAsync()
    {
        var lines = (await fixture.GetPostgresLogsAsync()).Split('\n');
        var reports = 0;
        var releaseVictims = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("ERROR:  deadlock detected", StringComparison.Ordinal))
            {
                continue;
            }

            reports++;
            // The victim's own statement is the `STATEMENT:` line Postgres appends to its report.
            var statement = Array.FindIndex(
                lines, i, Math.Min(30, lines.Length - i), l => l.Contains("STATEMENT:", StringComparison.Ordinal));
            if (statement >= 0 && lines.Skip(statement).Take(3).Any(l => l.Contains("active_chats - 1", StringComparison.Ordinal)))
            {
                releaseVictims++;
            }
        }

        return (reports, releaseVictims);
    }

    /// <summary>
    /// `23-04`'s own Done-when: "Closing a taken conversation releases exactly one slot (`6-09`'s
    /// existing test, extended)." Every test above this one proves the release for an *engine-made*
    /// claim; this is the same invariant for the other real writer of `HoldsCapacityClaim` -
    /// `AssignConversationHandler`'s own unconditional charge, reached the same way the console's rail
    /// reaches it now. Seeded independently of `SeedAsync`'s shared role (which grants only
    /// `conversation:close`) because this scenario needs `conversation:assign` too.
    /// </summary>
    [Fact]
    public async Task ClosingATakenConversation_ReleasesExactlyOneSlot()
    {
        const int capacity = 3;
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var roleId = Guid.NewGuid();

        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            seed.Visitors.Add(new Visitor(visitorId, siteId, Now));
            seed.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity));
            seed.Roles.Add(new RoleRecord
            {
                Id = roleId,
                SiteId = siteId,
                Name = "Operator",
                Permissions = [Permission.ConversationAssign.Value, Permission.ConversationClose.Value],
            });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
            seed.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var handler = new AssignConversationHandler(
                new ConversationRepository(db), new ConversationAssignmentLog(db), new PermissionChecker(db),
                new OperatorCapacityStore(db), new EfUnitOfWork(db), new UuidV7Generator(), new SystemClock());

            var result = await handler.HandleAsync(
                new AssignConversation(conversationId, operatorId, siteId), CancellationToken.None);
            Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : string.Empty);
        }

        Assert.Equal(1, await ActiveChatsAsync(operatorId));

        await using (var db = fixture.CreateDbContext())
        {
            var handler = new CloseConversationHandler(
                new ConversationRepository(db), new ConversationAssignmentLog(db), new PermissionChecker(db),
                new OperatorCapacityStore(db), new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator(),
                new SystemClock(), NullLogger<CloseConversationHandler>.Instance);

            var result = await handler.HandleAsync(
                new Application.UseCases.CloseConversation.CloseConversation(conversationId, operatorId, siteId),
                CancellationToken.None);
            Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : string.Empty);
        }

        // Exactly one slot released - not zero (the leak `6-09` fixed) and not more than one (an
        // over-release, which `ReleaseAsync`'s own floor at zero would otherwise mask).
        Assert.Equal(0, await ActiveChatsAsync(operatorId));

        await using var verify = fixture.CreateDbContext();
        var interval = await verify.ConversationAssignments.AsNoTracking().SingleAsync(i => i.ConversationId == conversationId);
        Assert.Equal(ConversationAssignmentSource.Taken, interval.Source);
        Assert.NotNull(interval.EndedAt);
    }

    private sealed record Seed(SiteId SiteId, IReadOnlyList<OperatorId> OperatorIds, VisitorId VisitorId);

    /// <summary>`6-08`'s seam, reused verbatim from MarkConversationReadConcurrencyTests: every read
    /// goes to the real repository untouched, and each of the first <paramref name="maxInjections"/>
    /// saves runs a real, fully-committed concurrent write first.</summary>
    private sealed class RacingConversationRepository(
        IConversationRepository inner, int maxInjections, Func<Task> injectConcurrentWriteAsync) : IConversationRepository
    {
        public int SaveAttempts { get; private set; }

        public Task<Conversation?> GetByIdAsync(ConversationId id, CancellationToken cancellationToken) =>
            inner.GetByIdAsync(id, cancellationToken);

        public Task<Conversation?> GetActiveForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken) =>
            inner.GetActiveForVisitorAsync(visitorId, cancellationToken);

        public Task<IReadOnlyList<Conversation>> GetAssignedToOperatorAsync(OperatorId operatorId, CancellationToken cancellationToken) =>
            inner.GetAssignedToOperatorAsync(operatorId, cancellationToken);

        public Task<IReadOnlyList<Conversation>> GetWaitingForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
            inner.GetWaitingForSiteAsync(siteId, cancellationToken);

        public async Task SaveAsync(Conversation conversation, CancellationToken cancellationToken)
        {
            SaveAttempts++;
            if (SaveAttempts <= maxInjections)
            {
                await injectConcurrentWriteAsync();
            }

            await inner.SaveAsync(conversation, cancellationToken);
        }
    }

    private async Task<Seed> SeedAsync(int operatorCount, int capacity, int conversationCount)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorIds = Enumerable.Range(0, operatorCount).Select(_ => new OperatorId(Guid.NewGuid())).ToList();
        var roleId = Guid.NewGuid();

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        db.Roles.Add(new RoleRecord
        {
            Id = roleId,
            SiteId = siteId,
            Name = "Operator",
            Permissions = [Permission.ConversationClose.Value],
        });

        foreach (var operatorId in operatorIds)
        {
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity));
            db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
        }

        for (var i = 0; i < conversationCount; i++)
        {
            db.Conversations.Add(Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, Now));
        }

        await db.SaveChangesAsync(CancellationToken.None);
        return new Seed(siteId, operatorIds, visitorId);
    }

    private ConversationAssignmentJob CreateAssignmentJob(int batchSize) =>
        new(fixture.DataSource,
            new SkipLockedAssignmentClaimer(fixture.DataSource, new SystemClock(), new UuidV7Generator()),
            Options.Create(new ConversationAssignmentJobOptions { BatchSize = batchSize }),
            NullLogger<ConversationAssignmentJob>.Instance);

    /// <summary>The real handler on its own <c>AgoChatDbContext</c>, exactly as one Api request gets
    /// it. Returns whether the close succeeded, so a caller can assert on a rejected replay.</summary>
    private async Task<bool> CloseAsync(Seed seed, ConversationId conversationId)
    {
        await using var db = fixture.CreateDbContext();
        var conversation = await db.Conversations.AsNoTracking().SingleAsync(c => c.Id == conversationId);
        if (conversation.OperatorId is not { } operatorId)
        {
            return false; // released back to the queue by a racing tick between the read and now
        }

        var handler = new CloseConversationHandler(
            new ConversationRepository(db), new ConversationAssignmentLog(db), new PermissionChecker(db),
            new OperatorCapacityStore(db), new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator(),
            new SystemClock(), NullLogger<CloseConversationHandler>.Instance);

        var result = await handler.HandleAsync(
            new Application.UseCases.CloseConversation.CloseConversation(conversationId, operatorId, seed.SiteId),
            CancellationToken.None);
        return result.IsSuccess;
    }

    /// <summary>Commits a visitor message on its own connection - a real `xmin` bump on the
    /// conversation row, the same one a concurrent send produces in production (`6-06`'s own
    /// finding).</summary>
    private async Task SendConcurrentVisitorMessageAsync(Seed seed, ConversationId conversationId)
    {
        await using var db = fixture.CreateDbContext();
        var repository = new ConversationRepository(db);
        var conversation = (await repository.GetByIdAsync(conversationId, CancellationToken.None))!;
        conversation.AddVisitorMessage(seed.VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("incoming"), Now);
        conversation.ClearDomainEvents();
        await repository.SaveAsync(conversation, CancellationToken.None);
    }

    private async Task<List<ConversationId>> AssignedIdsAsync(Seed seed)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Conversations.AsNoTracking()
            .Where(c => c.SiteId == seed.SiteId && c.State == ConversationState.Assigned)
            .OrderBy(c => c.Id)
            .Select(c => c.Id)
            .ToListAsync();
    }

    private async Task<List<ConversationId>> WaitingIdsAsync(Seed seed)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Conversations.AsNoTracking()
            .Where(c => c.SiteId == seed.SiteId && c.State == ConversationState.Waiting)
            .OrderBy(c => c.Id)
            .Select(c => c.Id)
            .ToListAsync();
    }

    private async Task<int> ActiveChatsAsync(OperatorId operatorId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Operators.AsNoTracking()
            .Where(o => o.Id == operatorId)
            .Select(o => EF.Property<int>(o, "active_chats"))
            .SingleAsync();
    }
}
