using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Module;
using Ago.Chat.Worker;
using Ago.Platform.Abstractions;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `26-108`'s Done-when, live against real Postgres (`ConnectionFanoutFixture`, already the right
/// container set - no RabbitMQ/Redis dependency of its own, since every publish here goes through
/// <see cref="RecordingEventPublisher"/> and every claim through <see cref="TokenBucketFakeRateLimiter"/>
/// rather than the real broker/Redis those two exist to stand in for). Calls
/// <see cref="OperatorDisconnectSweepJob.SweepAsync"/> directly, twice, rather than waiting on its
/// own `PeriodicTimer` - the bug this item fixes is about what happens *within* one tick's repeat
/// calls to <see cref="OperatorPresencePublisher.PublishLostAsync"/>, not about the timer itself.
///
/// The registry used here always reports zero connections (<see cref="AlwaysDisconnectedRegistry"/>),
/// so every test's seeded operator matches the sweep's query on every tick - each test's own
/// assertion is scoped to that operator's own <c>PartitionKey</c>, so leftover `Assigned` rows this
/// collection's other tests (`OperatorDisconnectGraceEndToEndTests`) may leave behind in the shared
/// Postgres container cannot make a test pass or fail for the wrong reason.
/// </summary>
[Collection(ConnectionFanoutCollection.Name)]
public sealed class OperatorPresenceLostDedupTests(ConnectionFanoutFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    [Fact]
    public async Task SweepAsync_CalledTwiceForTheSameStillDisconnectedOperator_PublishesOperatorPresenceLostExactlyOnce()
    {
        var (_, operatorId) = await SeedAssignedConversationAsync();
        var recorder = new RecordingEventPublisher();
        var sweepJob = BuildSweepJob(recorder, new TokenBucketFakeRateLimiter(), TimeSpan.FromSeconds(30));

        await sweepJob.SweepAsync(CancellationToken.None); // tick 1 - genuinely missed, publishes
        await sweepJob.SweepAsync(CancellationToken.None); // tick 2 - same operator, still gone, still Assigned

        Assert.Single(PublishedFor(recorder, operatorId));
    }

    [Fact]
    public async Task SweepAsync_ForAnOperatorWithNoPriorSuppressionMarker_StillPublishes()
    {
        // `26-108`'s backstop guarantee: dedup must never swallow the *first* publish for a
        // genuinely missed disconnect - only repeats within the same still-in-flight window.
        var (_, operatorId) = await SeedAssignedConversationAsync();
        var recorder = new RecordingEventPublisher();
        var sweepJob = BuildSweepJob(recorder, new TokenBucketFakeRateLimiter(), TimeSpan.FromSeconds(30));

        await sweepJob.SweepAsync(CancellationToken.None);

        Assert.Single(PublishedFor(recorder, operatorId));
    }

    [Fact]
    public async Task SweepAsync_ForTheSameOperator_AfterTheSuppressionMarkerExpires_PublishesAgain()
    {
        var (_, operatorId) = await SeedAssignedConversationAsync();
        var recorder = new RecordingEventPublisher();
        var ttl = TimeSpan.FromMilliseconds(300);
        var sweepJob = BuildSweepJob(recorder, new TokenBucketFakeRateLimiter(), ttl);

        await sweepJob.SweepAsync(CancellationToken.None); // claims the marker, publishes
        await Task.Delay(ttl + TimeSpan.FromMilliseconds(300)); // cross the TTL for real

        await sweepJob.SweepAsync(CancellationToken.None); // fresh genuine disconnect signal - publishes again

        Assert.Equal(2, PublishedFor(recorder, operatorId).Count);
    }

    [Fact]
    public async Task FastPathPublish_FollowedByASweepTickForTheSameOperator_DoesNotPublishASecondTime()
    {
        // `26-108`: OperatorHub.OnDisconnectedAsync's fast path and OperatorDisconnectSweepJob's
        // backstop must share one suppression key - both go through the one
        // OperatorPresencePublisher instance below, the same way ChatModule registers exactly one
        // per host for both callers to share.
        var (siteId, operatorId) = await SeedAssignedConversationAsync();
        var recorder = new RecordingEventPublisher();
        var presencePublisher = new OperatorPresencePublisher(
            recorder, new SystemClock(), new UuidV7Generator(),
            new TokenBucketFakeRateLimiter(), new OperatorPresenceLostSuppressionOptions { Ttl = TimeSpan.FromSeconds(30) });

        // Simulates OperatorHub.OnDisconnectedAsync's own immediate publish.
        await presencePublisher.PublishLostAsync(operatorId, siteId, CancellationToken.None);

        var sweepJob = new OperatorDisconnectSweepJob(
            fixture.DataSource, new AlwaysDisconnectedRegistry(), presencePublisher,
            Options.Create(new OperatorDisconnectSweepJobOptions()), NullLogger<OperatorDisconnectSweepJob>.Instance);
        await sweepJob.SweepAsync(CancellationToken.None); // the very next tick, before the marker expires

        Assert.Single(PublishedFor(recorder, operatorId));
    }

    private OperatorDisconnectSweepJob BuildSweepJob(RecordingEventPublisher recorder, IRateLimiter rateLimiter, TimeSpan suppressionTtl)
    {
        var presencePublisher = new OperatorPresencePublisher(
            recorder, new SystemClock(), new UuidV7Generator(), rateLimiter,
            new OperatorPresenceLostSuppressionOptions { Ttl = suppressionTtl });
        return new OperatorDisconnectSweepJob(
            fixture.DataSource, new AlwaysDisconnectedRegistry(), presencePublisher,
            Options.Create(new OperatorDisconnectSweepJobOptions()), NullLogger<OperatorDisconnectSweepJob>.Instance);
    }

    private static List<EventEnvelope> PublishedFor(RecordingEventPublisher recorder, OperatorId operatorId) =>
        recorder.Published.Where(e => e.PartitionKey == operatorId.Value.ToString()).ToList();

    private async Task<(SiteId SiteId, OperatorId OperatorId)> SeedAssignedConversationAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
            var roleId = Guid.NewGuid();
            db.Roles.Add(new RoleRecord { Id = roleId, SiteId = siteId, Name = "Operator", Permissions = [Permission.ConversationAssign.Value] });
            db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            var conversation = Conversation.Start(conversationId, siteId, visitorId, Now);
            conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
            conversation.AssignTo(operatorId, Now, holdsCapacityClaim: true);
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync();
        }

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("UPDATE operators SET active_chats = 1 WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", operatorId.Value);
        await command.ExecuteNonQueryAsync();

        return (siteId, operatorId);
    }

    /// <summary>Every operator this test seeds has zero connections, always - the sweep's own
    /// "zero connections" branch is what this whole item is about, not the registry lookup itself
    /// (`ReconnectResumeTests.NoOpConnectionRegistry` is the same shape, renamed here for what it
    /// actually asserts in this file).</summary>
    private sealed class AlwaysDisconnectedRegistry : IConnectionRegistry
    {
        public Task RegisterAsync(ConnectionId connectionId, NodeId nodeId, PrincipalKey principal, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task UnregisterAsync(ConnectionId connectionId, NodeId nodeId, PrincipalKey principal, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyCollection<RegisteredConnection>> GetConnectionsAsync(PrincipalKey principal, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<RegisteredConnection>>([]);

        public Task RemoveNodeAsync(NodeId nodeId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
