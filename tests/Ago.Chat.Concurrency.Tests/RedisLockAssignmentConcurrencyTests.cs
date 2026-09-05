using Ago.Chat.Domain;
using Ago.Chat.Worker;
using Ago.Platform.Caching.Redis;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>
/// `4-03`'s Done-when: the same guarantee `ConversationAssignmentConcurrencyTests` (`4-02`) proved
/// for mechanism A, proven again for mechanism B - multiple `ConversationAssignmentJob` instances
/// wired to `RedisLockAssignmentClaimer` (simulating multiple `Worker` replicas), running
/// concurrently against one real Postgres and one real Redis, never let an operator's
/// `active_chats` exceed `capacity` and never double-assign a conversation - not a weaker bar than
/// mechanism A's own.
/// </summary>
[Collection(SiteCachingConcurrencyCollection.Name)]
public sealed class RedisLockAssignmentConcurrencyTests(SiteCachingConcurrencyFixture fixture)
{
    // `23-05`: real, rounded UtcNow rather than a fixed historical date - see
    // ConversationAssignmentConcurrencyTests's own remarks on this identical field for why.
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

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

        var redisLock = new RedisDistributedLock(
            fixture.RedisMultiplexer, new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(2)).Build(),
            NullLogger<RedisDistributedLock>.Instance);
        var jobOptions = Options.Create(new ConversationAssignmentJobOptions { BatchSize = conversationCount });
        var jobs = Enumerable.Range(0, replicaCount)
            .Select(_ => new ConversationAssignmentJob(
                fixture.DataSource,
                new RedisLockAssignmentClaimer(redisLock, fixture.DataSource, new SystemClock(), new UuidV7Generator()),
                jobOptions,
                NullLogger<ConversationAssignmentJob>.Instance))
            .ToList();

        // Several concurrent ticks, matching 4-02's own reasoning: a conversation whose attempt lost
        // (capacity race, or an xmin optimistic-concurrency conflict - see
        // RedisLockAssignmentClaimer's own remarks) only gets retried next tick.
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

        Assert.All(assigned, c => Assert.Contains(c.OperatorId!.Value, operatorIds));

        var operators = await verify.Operators.AsNoTracking()
            .Where(o => operatorIds.Contains(o.Id))
            .Select(o => new { o.Id, ActiveChats = EF.Property<int>(o, "active_chats") })
            .ToListAsync();
        Assert.All(operators, o => Assert.InRange(o.ActiveChats, 0, operatorCapacity));

        var totalCapacity = operatorCount * operatorCapacity;
        Assert.Equal(Math.Min(conversationCount, totalCapacity), assigned.Count);
        Assert.Equal(assigned.Count, operators.Sum(o => o.ActiveChats));

        // `23-03`'s own Done-when, mechanism B's own proof - see ConversationAssignmentConcurrencyTests'
        // identical assertion for mechanism A. RedisLockAssignmentClaimer writes the interval as raw
        // SQL in the same transaction as its own claim and save.
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
