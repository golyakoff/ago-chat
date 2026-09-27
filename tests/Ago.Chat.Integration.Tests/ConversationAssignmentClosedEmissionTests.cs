using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `26-237`/`adr/0186` (operator-load rollup, decision B): the concurrent-load-at-close computation, against
/// a real Postgres. When an assignment interval closes, <see cref="ConversationAssignmentLog"/> resolves the
/// operator's own concurrent load at the interval's <em>start</em> (the identical overlap the live report
/// computes, via <see cref="ConversationAssignmentOverlapQuery.CountHeldAtAsync"/>), the operator's capacity,
/// and the interval's first operator reply, and stages a <see cref="ConversationAssignmentClosed"/> event to
/// the outbox (rule 4). This proves that emission carries the hand-computed ground truth - not merely that a
/// row is enqueued.
///
/// <para><b>The scenario.</b> Operator A, capacity 2. The interval being closed (I1, conversation C1) starts
/// at <c>T0</c>; a second interval of A's (I2) was already open from <c>T0-5m</c>, and a third (I3) does not
/// start until <c>T0+5m</c>. A fourth interval belongs to operator B over the same window. So A's concurrent
/// load <em>at I1's start</em> is 2 (I1 + I2; I3 has not started, B is a different operator) - exactly
/// capacity, so not additional. A replies in C1 30s after it starts; I1 is closed 10 minutes later, so the
/// reply falls inside the interval.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ConversationAssignmentClosedEmissionTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset T0 =
        new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    [Fact]
    public async Task ClosingAnInterval_EmitsConversationAssignmentClosed_WithTheConcurrentLoadCapacityAndFirstReplyResolvedAtClose()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorA = new OperatorId(Guid.NewGuid());
        var operatorB = new OperatorId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Operators.Add(new Operator(operatorA, siteId, OperatorStatus.Online, capacity: 2));
            db.Operators.Add(new Operator(operatorB, siteId, OperatorStatus.Online, capacity: 5));
            await db.SaveChangesAsync();
        }

        // C1: A holds it and replies 30s in - the interval this test closes. Its aggregate writes the real
        // operator message the reply query reads.
        var c1 = await SeedRepliedConversationAsync(siteId, operatorA, startedAt: T0, replySeconds: 30);

        // The overlap plan around I1's start (T0): I2 already open from T0-5m (counts), I3 opens at T0+5m
        // (does not count), operator B's interval over the same window (must never count).
        AddOpenInterval(siteId, operatorA, c1, T0);                       // I1 - the one being closed
        AddOpenInterval(siteId, operatorA, new ConversationId(Guid.NewGuid()), T0.AddMinutes(-5)); // I2
        AddOpenInterval(siteId, operatorA, new ConversationId(Guid.NewGuid()), T0.AddMinutes(5));  // I3
        AddOpenInterval(siteId, operatorB, new ConversationId(Guid.NewGuid()), T0.AddMinutes(-5)); // B
        await FlushIntervalsAsync();

        var endedAt = T0.AddMinutes(10);
        var outbox = new CapturingOutboxWriter();

        await using (var db = fixture.CreateDbContext())
        {
            var log = new ConversationAssignmentLog(db, fixture.DataSource, outbox, new UuidV7Generator());
            await log.CloseOpenAsync(c1, endedAt, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        var envelope = Assert.Single(outbox.Enqueued);
        Assert.Equal(nameof(ConversationAssignmentClosed), envelope.Type);
        // OccurredAt is the interval's start, so the raw layer buckets it into the day the holding period began.
        Assert.Equal(T0, envelope.OccurredAt);

        var payload = JsonSerializer.Deserialize<ConversationAssignmentClosed>(envelope.Payload)!;
        Assert.Equal(c1.Value, payload.ConversationId);
        Assert.Equal(siteId.Value, payload.SiteId);
        Assert.Equal(operatorA.Value, payload.OperatorId);
        Assert.Equal(T0, payload.StartedAt);
        Assert.Equal(endedAt, payload.EndedAt);
        Assert.Equal(2, payload.ConcurrentLoad);   // I1 + I2 at T0; I3 not yet started, B is another operator
        Assert.Equal(2, payload.Capacity);          // operator A's capacity
        Assert.Equal(T0.AddSeconds(30), payload.FirstReplyAt);
        Assert.Equal("Europe/Moscow", payload.TenantZone);
    }

    [Fact]
    public async Task ClosingAConversationWithNoOpenInterval_EmitsNothing()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            await db.SaveChangesAsync();
        }

        var outbox = new CapturingOutboxWriter();
        await using (var db = fixture.CreateDbContext())
        {
            var log = new ConversationAssignmentLog(db, fixture.DataSource, outbox, new UuidV7Generator());
            // A conversation that never had an assignment interval - the honest no-op the port documents.
            await log.CloseOpenAsync(new ConversationId(Guid.NewGuid()), T0, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        Assert.Empty(outbox.Enqueued);
    }

    [Fact]
    public async Task ClosingAnIntervalWithNoOperatorReplyInIt_EmitsANullFirstReplyAt()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorA = new OperatorId(Guid.NewGuid());
        var c = new ConversationId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Operators.Add(new Operator(operatorA, siteId, OperatorStatus.Online, capacity: 3));
            await db.SaveChangesAsync();
        }

        AddOpenInterval(siteId, operatorA, c, T0);
        await FlushIntervalsAsync();

        var outbox = new CapturingOutboxWriter();
        await using (var db = fixture.CreateDbContext())
        {
            var log = new ConversationAssignmentLog(db, fixture.DataSource, outbox, new UuidV7Generator());
            await log.CloseOpenAsync(c, T0.AddMinutes(5), CancellationToken.None);
            await db.SaveChangesAsync();
        }

        var payload = JsonSerializer.Deserialize<ConversationAssignmentClosed>(Assert.Single(outbox.Enqueued).Payload)!;
        Assert.Null(payload.FirstReplyAt);      // no operator message in this conversation at all
        Assert.Equal(1, payload.ConcurrentLoad); // only its own interval
    }

    private readonly List<ConversationAssignmentInterval> _pendingIntervals = [];

    private void AddOpenInterval(SiteId siteId, OperatorId operatorId, ConversationId conversationId, DateTimeOffset startedAt) =>
        _pendingIntervals.Add(ConversationAssignmentInterval.Open(
            new ConversationAssignmentId(Guid.NewGuid()), siteId, conversationId, operatorId,
            ConversationAssignmentSource.Assigned, startedAt));

    private async Task FlushIntervalsAsync()
    {
        await using var db = fixture.CreateDbContext();
        db.ConversationAssignments.AddRange(_pendingIntervals);
        await db.SaveChangesAsync();
        _pendingIntervals.Clear();
    }

    private async Task<ConversationId> SeedRepliedConversationAsync(
        SiteId siteId, OperatorId operatorId, DateTimeOffset startedAt, int replySeconds)
    {
        var visitorId = new VisitorId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Visitors.Add(new Visitor(visitorId, siteId, startedAt));
            await db.SaveChangesAsync();
        }

        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, startedAt);
        conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hello"), startedAt);
        conversation.AssignTo(operatorId, startedAt);
        conversation.AddOperatorMessage(
            operatorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), startedAt.AddSeconds(replySeconds));

        await using var writeDb = fixture.CreateDbContext();
        writeDb.Conversations.Add(conversation);
        await writeDb.SaveChangesAsync();
        return conversation.Id;
    }

    private sealed class CapturingOutboxWriter : IOutboxWriter
    {
        private readonly List<EventEnvelope> _enqueued = [];
        public IReadOnlyList<EventEnvelope> Enqueued => _enqueued;
        public void Enqueue(EventEnvelope envelope, string? traceContext = null) => _enqueued.Add(envelope);
    }
}
