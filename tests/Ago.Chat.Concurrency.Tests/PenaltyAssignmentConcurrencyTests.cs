using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Worker;
using Ago.Platform.Caching.Redis;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Polly;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>
/// `23-05`'s own Done-when, proven against real Postgres for `SkipLockedAssignmentClaimer` -
/// mechanism A. <see cref="RedisLockPenaltyAssignmentConcurrencyTests"/> right below proves the
/// identical set for mechanism B, because the item is explicit that the two `IAssignmentClaimer`
/// implementations "must not diverge" - a defect in one and not the other is exactly what these two
/// mirrored classes exist to catch.
/// </summary>
[Collection(ConcurrencyCollection.Name)]
public sealed class SkipLockedPenaltyAssignmentConcurrencyTests(ConcurrencyTestFixture fixture)
{
    private static SkipLockedAssignmentClaimer CreateClaimer(ConcurrencyTestFixture fixture) =>
        new(fixture.DataSource, new SystemClock(), new UuidV7Generator());

    private async Task<(SiteId SiteId, OperatorId OperatorId, ConversationId ConversationId)> SeedOneOperatorAtCapacityAsync(
        DateTimeOffset conversationCreatedAt, OperatorStatus operatorStatus = OperatorStatus.Online, int? penaltySeconds = null)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            var site = new Site(siteId, $"site_{siteId.Value:N}", []);
            if (penaltySeconds is { } seconds)
            {
                site.UpdateAssignmentPenalty(seconds, conversationCreatedAt);
            }

            db.Sites.Add(site);
            db.Operators.Add(new Operator(operatorId, siteId, operatorStatus, capacity: 1));
            db.Visitors.Add(new Visitor(visitorId, siteId, conversationCreatedAt));
            db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, conversationCreatedAt));
            await db.SaveChangesAsync();
        }

        // Fill the operator's only slot - production code (OperatorCapacityStore), not a raw UPDATE
        // of my own, so "at capacity" here means exactly what the claimer's own first pass checks.
        await using (var db = fixture.CreateDbContext())
        {
            await new OperatorCapacityStore(db).ClaimAsync(operatorId, CancellationToken.None);
        }

        return (siteId, operatorId, conversationId);
    }

    [Fact]
    public async Task YoungerThanThePenalty_IsNotAssignedOverCapacity()
    {
        var seedNow = DateTimeOffset.UtcNow;
        var (siteId, _, conversationId) = await SeedOneOperatorAtCapacityAsync(seedNow.AddSeconds(-10));

        await CreateClaimer(fixture).AssignWaitingConversationsAsync(siteId, batchSize: 10, CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var conversation = await verify.Conversations.AsNoTracking().SingleAsync(c => c.Id == conversationId);
        Assert.Equal(ConversationState.Waiting, conversation.State);
        Assert.Empty(await verify.ConversationAssignments.AsNoTracking()
            .Where(a => a.ConversationId == conversationId).ToListAsync());
    }

    [Fact]
    public async Task OlderThanThePenalty_IsAssignedToTheLeastLoadedOnlineOperator_SourceAdditional()
    {
        var seedNow = DateTimeOffset.UtcNow;
        // 1s penalty: seeded 10s in the past is comfortably past it, without a real test waiting
        // anywhere near two real minutes.
        var (siteId, operatorId, conversationId) =
            await SeedOneOperatorAtCapacityAsync(seedNow.AddSeconds(-10), penaltySeconds: 1);

        var assigned = await CreateClaimer(fixture).AssignWaitingConversationsAsync(siteId, batchSize: 10, CancellationToken.None);

        Assert.Equal(1, assigned);
        await using var verify = fixture.CreateDbContext();
        var conversation = await verify.Conversations.AsNoTracking().SingleAsync(c => c.Id == conversationId);
        Assert.Equal(ConversationState.Assigned, conversation.State);
        Assert.Equal(operatorId, conversation.OperatorId);

        var interval = await verify.ConversationAssignments.AsNoTracking()
            .SingleAsync(a => a.ConversationId == conversationId);
        Assert.Equal(ConversationAssignmentSource.Additional, interval.Source);
        Assert.Null(interval.EndedAt);

        // The whole point of this pass: capacity did not stop it.
        var op = await verify.Operators.AsNoTracking()
            .Select(o => new { o.Id, ActiveChats = EF.Property<int>(o, "active_chats") })
            .SingleAsync(o => o.Id == operatorId);
        Assert.Equal(2, op.ActiveChats); // capacity was 1
    }

    [Theory]
    [InlineData(OperatorStatus.Offline)]
    [InlineData(OperatorStatus.Away)]
    public async Task WithNoOperatorOnline_NothingIsAssigned_RegardlessOfAge(OperatorStatus status)
    {
        var seedNow = DateTimeOffset.UtcNow;
        var (siteId, _, conversationId) =
            await SeedOneOperatorAtCapacityAsync(seedNow.AddSeconds(-10), operatorStatus: status, penaltySeconds: 1);

        var assigned = await CreateClaimer(fixture).AssignWaitingConversationsAsync(siteId, batchSize: 10, CancellationToken.None);

        Assert.Equal(0, assigned);
        await using var verify = fixture.CreateDbContext();
        var conversation = await verify.Conversations.AsNoTracking().SingleAsync(c => c.Id == conversationId);
        Assert.Equal(ConversationState.Waiting, conversation.State);
        Assert.Empty(await verify.ConversationAssignments.AsNoTracking()
            .Where(a => a.ConversationId == conversationId).ToListAsync());
    }

    [Fact]
    public async Task TwoSitesWithDifferentPenalties_BehaveDifferently()
    {
        var seedNow = DateTimeOffset.UtcNow;
        var conversationAge = seedNow.AddSeconds(-30);

        var (shortPenaltySite, _, oldEnoughConversation) =
            await SeedOneOperatorAtCapacityAsync(conversationAge, penaltySeconds: 5); // 30s old > 5s penalty
        var (longPenaltySite, _, tooYoungConversation) =
            await SeedOneOperatorAtCapacityAsync(conversationAge, penaltySeconds: 3600); // 30s old < 1h penalty

        var claimer = CreateClaimer(fixture);
        await claimer.AssignWaitingConversationsAsync(shortPenaltySite, batchSize: 10, CancellationToken.None);
        await claimer.AssignWaitingConversationsAsync(longPenaltySite, batchSize: 10, CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var assignedOne = await verify.Conversations.AsNoTracking().SingleAsync(c => c.Id == oldEnoughConversation);
        var stillWaiting = await verify.Conversations.AsNoTracking().SingleAsync(c => c.Id == tooYoungConversation);
        Assert.Equal(ConversationState.Assigned, assignedOne.State);
        Assert.Equal(ConversationState.Waiting, stillWaiting.State);
    }

    /// <summary>
    /// `CLAUDE.md` rule 8, proven rather than merely asserted: the site's own penalty is updated by a
    /// bare `UPDATE sites ...` on a second connection - deliberately bypassing
    /// `UpdateAssignmentPenaltyHandler`, the `Site` aggregate, the outbox and every cache-invalidation
    /// event this codebase has - and the very next claimer tick already sees it. If the claimer read
    /// this value from any cache (or from a copy taken earlier in its own lifetime), this update
    /// would have nothing to invalidate that cache and the assertion below would fail.
    /// </summary>
    [Fact]
    public async Task PenaltyChangedDirectlyInPostgres_TakesEffectOnTheVeryNextTick_NoCacheToInvalidate()
    {
        var seedNow = DateTimeOffset.UtcNow;
        // Seeded with a huge penalty - the first tick must find nothing old enough.
        var (siteId, operatorId, conversationId) =
            await SeedOneOperatorAtCapacityAsync(seedNow.AddSeconds(-30), penaltySeconds: 3600);

        var claimer = CreateClaimer(fixture);
        var firstTick = await claimer.AssignWaitingConversationsAsync(siteId, batchSize: 10, CancellationToken.None);
        Assert.Equal(0, firstTick);

        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            "UPDATE sites SET assignment_penalty_seconds = 1 WHERE id = @id", connection))
        {
            command.Parameters.AddWithValue("id", siteId.Value);
            await command.ExecuteNonQueryAsync();
        }

        var secondTick = await claimer.AssignWaitingConversationsAsync(siteId, batchSize: 10, CancellationToken.None);
        Assert.Equal(1, secondTick);

        await using var verify = fixture.CreateDbContext();
        var conversation = await verify.Conversations.AsNoTracking().SingleAsync(c => c.Id == conversationId);
        Assert.Equal(ConversationState.Assigned, conversation.State);
        Assert.Equal(operatorId, conversation.OperatorId);
    }

    [Fact]
    public async Task TwoConcurrentReplicaTicks_OnOneOldWaitingConversation_ProduceExactlyOneAssignmentAndOneInterval()
    {
        var seedNow = DateTimeOffset.UtcNow;
        var (siteId, _, conversationId) =
            await SeedOneOperatorAtCapacityAsync(seedNow.AddSeconds(-10), penaltySeconds: 1);

        // `ConversationAssignmentJob`, not the claimer directly - matching
        // ConversationAssignmentConcurrencyTests's own precedent: the job's own per-tick catch is what
        // turns a claimer's internal contention (including a raw Postgres unique-violation on the
        // interval insert, `concurrency.md`'s own "second, unrelated gap" for a claimer racing itself
        // over one conversation) into "retry next tick" rather than an unhandled exception - the
        // production shape this test exercises, not a lower-level call the real system never makes
        // unwrapped.
        var jobOptions = Options.Create(new ConversationAssignmentJobOptions { BatchSize = 10 });
        var jobs = Enumerable.Range(0, 3)
            .Select(_ => new ConversationAssignmentJob(
                fixture.DataSource, CreateClaimer(fixture), jobOptions, NullLogger<ConversationAssignmentJob>.Instance))
            .ToList();
        for (var tick = 0; tick < 3; tick++)
        {
            await Task.WhenAll(jobs.Select(job => job.RunOnceAsync(CancellationToken.None)));
        }

        await using var verify = fixture.CreateDbContext();
        var conversation = await verify.Conversations.AsNoTracking().SingleAsync(c => c.Id == conversationId);
        Assert.Equal(ConversationState.Assigned, conversation.State);

        var intervals = await verify.ConversationAssignments.AsNoTracking()
            .Where(a => a.ConversationId == conversationId).ToListAsync();
        var interval = Assert.Single(intervals);
        Assert.Equal(ConversationAssignmentSource.Additional, interval.Source);
        Assert.Equal(conversation.OperatorId, interval.OperatorId);
    }
}

/// <summary>Mechanism B's own proof of the identical set above - see the sibling class's own remarks.
/// The second pass takes no Redis lock (`RedisLockAssignmentClaimer`'s own remarks explain why), so
/// this suite's own contention is carried entirely by Postgres - the conversation's own `xmin` check.
/// </summary>
[Collection(SiteCachingConcurrencyCollection.Name)]
public sealed class RedisLockPenaltyAssignmentConcurrencyTests(SiteCachingConcurrencyFixture fixture)
{
    private RedisLockAssignmentClaimer CreateClaimer() =>
        new(
            new RedisDistributedLock(
                fixture.RedisMultiplexer, new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(2)).Build(),
                NullLogger<RedisDistributedLock>.Instance),
            fixture.DataSource, new SystemClock(), new UuidV7Generator());

    private async Task<(SiteId SiteId, OperatorId OperatorId, ConversationId ConversationId)> SeedOneOperatorAtCapacityAsync(
        DateTimeOffset conversationCreatedAt, OperatorStatus operatorStatus = OperatorStatus.Online, int? penaltySeconds = null)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            var site = new Site(siteId, $"site_{siteId.Value:N}", []);
            if (penaltySeconds is { } seconds)
            {
                site.UpdateAssignmentPenalty(seconds, conversationCreatedAt);
            }

            db.Sites.Add(site);
            db.Operators.Add(new Operator(operatorId, siteId, operatorStatus, capacity: 1));
            db.Visitors.Add(new Visitor(visitorId, siteId, conversationCreatedAt));
            db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, conversationCreatedAt));
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            await new OperatorCapacityStore(db).ClaimAsync(operatorId, CancellationToken.None);
        }

        return (siteId, operatorId, conversationId);
    }

    [Fact]
    public async Task YoungerThanThePenalty_IsNotAssignedOverCapacity()
    {
        var seedNow = DateTimeOffset.UtcNow;
        var (siteId, _, conversationId) = await SeedOneOperatorAtCapacityAsync(seedNow.AddSeconds(-10));

        await CreateClaimer().AssignWaitingConversationsAsync(siteId, batchSize: 10, CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var conversation = await verify.Conversations.AsNoTracking().SingleAsync(c => c.Id == conversationId);
        Assert.Equal(ConversationState.Waiting, conversation.State);
    }

    [Fact]
    public async Task OlderThanThePenalty_IsAssignedToTheLeastLoadedOnlineOperator_SourceAdditional()
    {
        var seedNow = DateTimeOffset.UtcNow;
        var (siteId, operatorId, conversationId) =
            await SeedOneOperatorAtCapacityAsync(seedNow.AddSeconds(-10), penaltySeconds: 1);

        var assigned = await CreateClaimer().AssignWaitingConversationsAsync(siteId, batchSize: 10, CancellationToken.None);

        Assert.Equal(1, assigned);
        await using var verify = fixture.CreateDbContext();
        var conversation = await verify.Conversations.AsNoTracking().SingleAsync(c => c.Id == conversationId);
        Assert.Equal(ConversationState.Assigned, conversation.State);
        Assert.Equal(operatorId, conversation.OperatorId);

        var interval = await verify.ConversationAssignments.AsNoTracking()
            .SingleAsync(a => a.ConversationId == conversationId);
        Assert.Equal(ConversationAssignmentSource.Additional, interval.Source);
        Assert.Null(interval.EndedAt);

        var op = await verify.Operators.AsNoTracking()
            .Select(o => new { o.Id, ActiveChats = EF.Property<int>(o, "active_chats") })
            .SingleAsync(o => o.Id == operatorId);
        Assert.Equal(2, op.ActiveChats);
    }

    [Theory]
    [InlineData(OperatorStatus.Offline)]
    [InlineData(OperatorStatus.Away)]
    public async Task WithNoOperatorOnline_NothingIsAssigned_RegardlessOfAge(OperatorStatus status)
    {
        var seedNow = DateTimeOffset.UtcNow;
        var (siteId, _, conversationId) =
            await SeedOneOperatorAtCapacityAsync(seedNow.AddSeconds(-10), operatorStatus: status, penaltySeconds: 1);

        var assigned = await CreateClaimer().AssignWaitingConversationsAsync(siteId, batchSize: 10, CancellationToken.None);

        Assert.Equal(0, assigned);
        await using var verify = fixture.CreateDbContext();
        var conversation = await verify.Conversations.AsNoTracking().SingleAsync(c => c.Id == conversationId);
        Assert.Equal(ConversationState.Waiting, conversation.State);
    }

    [Fact]
    public async Task TwoSitesWithDifferentPenalties_BehaveDifferently()
    {
        var seedNow = DateTimeOffset.UtcNow;
        var conversationAge = seedNow.AddSeconds(-30);

        var (shortPenaltySite, _, oldEnoughConversation) =
            await SeedOneOperatorAtCapacityAsync(conversationAge, penaltySeconds: 5);
        var (longPenaltySite, _, tooYoungConversation) =
            await SeedOneOperatorAtCapacityAsync(conversationAge, penaltySeconds: 3600);

        var claimer = CreateClaimer();
        await claimer.AssignWaitingConversationsAsync(shortPenaltySite, batchSize: 10, CancellationToken.None);
        await claimer.AssignWaitingConversationsAsync(longPenaltySite, batchSize: 10, CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var assignedOne = await verify.Conversations.AsNoTracking().SingleAsync(c => c.Id == oldEnoughConversation);
        var stillWaiting = await verify.Conversations.AsNoTracking().SingleAsync(c => c.Id == tooYoungConversation);
        Assert.Equal(ConversationState.Assigned, assignedOne.State);
        Assert.Equal(ConversationState.Waiting, stillWaiting.State);
    }

    /// <summary>`CLAUDE.md` rule 8, the identical proof the sibling class's own test gives for
    /// mechanism A - a bare `UPDATE sites ...` on a second connection, bypassing every
    /// application-level write path and cache-invalidation event this codebase has, takes effect on
    /// the very next tick.</summary>
    [Fact]
    public async Task PenaltyChangedDirectlyInPostgres_TakesEffectOnTheVeryNextTick_NoCacheToInvalidate()
    {
        var seedNow = DateTimeOffset.UtcNow;
        var (siteId, operatorId, conversationId) =
            await SeedOneOperatorAtCapacityAsync(seedNow.AddSeconds(-30), penaltySeconds: 3600);

        var claimer = CreateClaimer();
        var firstTick = await claimer.AssignWaitingConversationsAsync(siteId, batchSize: 10, CancellationToken.None);
        Assert.Equal(0, firstTick);

        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            "UPDATE sites SET assignment_penalty_seconds = 1 WHERE id = @id", connection))
        {
            command.Parameters.AddWithValue("id", siteId.Value);
            await command.ExecuteNonQueryAsync();
        }

        var secondTick = await claimer.AssignWaitingConversationsAsync(siteId, batchSize: 10, CancellationToken.None);
        Assert.Equal(1, secondTick);

        await using var verify = fixture.CreateDbContext();
        var conversation = await verify.Conversations.AsNoTracking().SingleAsync(c => c.Id == conversationId);
        Assert.Equal(ConversationState.Assigned, conversation.State);
        Assert.Equal(operatorId, conversation.OperatorId);
    }

    [Fact]
    public async Task TwoConcurrentReplicaTicks_OnOneOldWaitingConversation_ProduceExactlyOneAssignmentAndOneInterval()
    {
        var seedNow = DateTimeOffset.UtcNow;
        var (siteId, _, conversationId) =
            await SeedOneOperatorAtCapacityAsync(seedNow.AddSeconds(-10), penaltySeconds: 1);

        // `ConversationAssignmentJob`, not the claimer directly - see the sibling class's own remarks
        // on this identical test for why.
        var jobOptions = Options.Create(new ConversationAssignmentJobOptions { BatchSize = 10 });
        var jobs = Enumerable.Range(0, 3)
            .Select(_ => new ConversationAssignmentJob(
                fixture.DataSource, CreateClaimer(), jobOptions, NullLogger<ConversationAssignmentJob>.Instance))
            .ToList();
        for (var tick = 0; tick < 3; tick++)
        {
            await Task.WhenAll(jobs.Select(job => job.RunOnceAsync(CancellationToken.None)));
        }

        await using var verify = fixture.CreateDbContext();
        var conversation = await verify.Conversations.AsNoTracking().SingleAsync(c => c.Id == conversationId);
        Assert.Equal(ConversationState.Assigned, conversation.State);

        var intervals = await verify.ConversationAssignments.AsNoTracking()
            .Where(a => a.ConversationId == conversationId).ToListAsync();
        var interval = Assert.Single(intervals);
        Assert.Equal(ConversationAssignmentSource.Additional, interval.Source);
        Assert.Equal(conversation.OperatorId, interval.OperatorId);
    }
}
