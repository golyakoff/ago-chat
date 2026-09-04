using Ago.Chat.Domain;
using Ago.Chat.Worker;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>
/// `4-02`'s Done-when: multiple `ConversationAssignmentJob` instances (simulating multiple `Worker`
/// replicas) running concurrently against one real Postgres - no operator's `active_chats` ever
/// exceeds its `capacity`, no conversation is ever assigned twice, and every conversation ends up
/// either `Assigned` or still `Waiting` - never anything in between (`concurrency.md`'s own test
/// description, "fires K... from M threads... asserts... repeated under stress", applied to
/// assignment claims instead of message sequences).
/// </summary>
[Collection(ConcurrencyCollection.Name)]
public sealed class ConversationAssignmentConcurrencyTests(ConcurrencyTestFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MultipleConcurrentJobInstances_NeverExceedCapacity_AndNeverDoubleAssign()
    {
        const int conversationCount = 30;
        const int operatorCount = 3;
        const int operatorCapacity = 5; // 15 total slots - fewer than 30 waiting conversations, on purpose
        const int replicaCount = 3;

        var siteId = new SiteId(Guid.NewGuid());
        var operatorIds = Enumerable.Range(0, operatorCount).Select(_ => new OperatorId(Guid.NewGuid())).ToList();
        var conversationIds = Enumerable.Range(0, conversationCount).Select(_ => new ConversationId(Guid.NewGuid())).ToList();

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            foreach (var operatorId in operatorIds)
            {
                db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, operatorCapacity));
            }

            foreach (var conversationId in conversationIds)
            {
                var visitorId = new VisitorId(Guid.NewGuid());
                db.Visitors.Add(new Visitor(visitorId, siteId, Now));
                db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
            }

            await db.SaveChangesAsync();
        }

        var jobOptions = Options.Create(new ConversationAssignmentJobOptions { BatchSize = conversationCount });
        // A fresh SkipLockedAssignmentClaimer per job instance, matching one per real Worker
        // replica - each claimer is stateless beyond the shared NpgsqlDataSource pool, so this is
        // just making the "multiple replicas" simulation explicit, not required for correctness.
        var jobs = Enumerable.Range(0, replicaCount)
            .Select(_ => new ConversationAssignmentJob(
                fixture.DataSource,
                new SkipLockedAssignmentClaimer(fixture.DataSource, new SystemClock(), new UuidV7Generator()),
                jobOptions,
                NullLogger<ConversationAssignmentJob>.Instance))
            .ToList();

        // Several concurrent ticks, not one: a conversation whose top candidate lost the capacity
        // race (or whose whole batch hit a transaction-level deadlock, see ConversationAssignmentJob's
        // own remarks) only gets retried on a later tick, by design - the claim is "eventually
        // assigned if capacity exists," not "assigned on the very first attempt."
        for (var tick = 0; tick < 5; tick++)
        {
            await Task.WhenAll(jobs.Select(job => job.RunOnceAsync(CancellationToken.None)));
        }

        await using var verify = fixture.CreateDbContext();
        var conversations = await verify.Conversations.AsNoTracking()
            .Where(c => conversationIds.Contains(c.Id))
            .ToListAsync();

        var assigned = conversations.Where(c => c.State == ConversationState.Assigned).ToList();
        var stillWaiting = conversations.Where(c => c.State == ConversationState.Waiting).ToList();
        Assert.DoesNotContain(conversations, c => c.State == ConversationState.Closed);
        Assert.Equal(conversationCount, assigned.Count + stillWaiting.Count);

        // Every assigned conversation names exactly one of the seeded operators - no phantom
        // assignment, and (by SQL uniqueness alone this cannot double-count) no conversation
        // assigned twice.
        Assert.All(assigned, c => Assert.Contains(c.OperatorId!.Value, operatorIds));

        var operators = await verify.Operators.AsNoTracking()
            .Where(o => operatorIds.Contains(o.Id))
            .Select(o => new { o.Id, ActiveChats = EF.Property<int>(o, "active_chats") })
            .ToListAsync();
        Assert.All(operators, o => Assert.InRange(o.ActiveChats, 0, operatorCapacity));

        // Capacity is the hard ceiling: exactly min(demand, total capacity) conversations should end
        // up assigned once retries have had several ticks to settle.
        var totalCapacity = operatorCount * operatorCapacity;
        Assert.Equal(Math.Min(conversationCount, totalCapacity), assigned.Count);
        Assert.Equal(assigned.Count, operators.Sum(o => o.ActiveChats));

        // `23-03`'s own Done-when: "Two Worker replicas racing one conversation produce exactly one
        // assignment and exactly one interval." Every assigned conversation has exactly one open,
        // Assigned-sourced interval naming its own operator; a still-Waiting conversation - never
        // claimed by anyone - has none. SkipLockedAssignmentClaimer writes the interval as raw SQL in
        // the identical transaction as the claim (ConversationAssignmentIntervalSql's own remarks), so
        // this is also the proof that the raw-SQL path is exactly as atomic as the port-based one.
        var intervals = await verify.ConversationAssignments.AsNoTracking()
            .Where(a => conversationIds.Contains(a.ConversationId))
            .ToListAsync();
        Assert.Equal(assigned.Count, intervals.Count);
        foreach (var conversation in assigned)
        {
            var interval = Assert.Single(intervals, i => i.ConversationId == conversation.Id);
            Assert.Equal(conversation.OperatorId, interval.OperatorId);
            Assert.Equal(ConversationAssignmentSource.Assigned, interval.Source);
            Assert.Null(interval.EndedAt);
        }

        Assert.DoesNotContain(intervals, i => stillWaiting.Select(c => c.Id).Contains(i.ConversationId));
    }
}
