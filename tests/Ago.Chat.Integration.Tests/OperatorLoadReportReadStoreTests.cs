using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-17`'s own Done-when, against a real Postgres: a hand-built plan of assignment intervals with a
/// known overlap, and every number <see cref="OperatorLoadReportReadStore"/> returns must equal ground
/// truth computed by hand - not merely "the query runs and returns something the right shape."
///
/// <para><b>The scenario.</b> Operator A, capacity 2. Four intervals, three conversations:</para>
/// <list type="bullet">
/// <item><b>C1 / interval A1</b>: <c>[T0, open)</c>. The only thing A holds at <c>T0</c> - concurrent
/// load 1, standard. A replies 30s after the visitor's own message.</item>
/// <item><b>C2 / interval A2</b>: <c>[T0+1m, T0+3m)</c> - A holds it first. A1 is still open, so the
/// concurrent load counting A2 itself is 2 - exactly capacity, still standard (`23-03`'s own naming
/// rule: the second only happens once capacity is <em>full</em>, not merely reached). A replies 20s
/// after A2 starts, then C2 is transferred to operator B (a second, capacity-5 operator whose own
/// numbers this test never asserts on - present only to prove the transfer does not corrupt A's own
/// count) and, later, transferred back to A.</item>
/// <item><b>C3 / interval A4</b>: <c>[T0+2m, open)</c>, started while A1 <em>and</em> A2 are both still
/// open - concurrent load counting A4 itself is 3, strictly over capacity: additional. A replies 50s
/// after A4 starts. It never closes within this scenario.</item>
/// <item><b>C2 / interval A3</b> (the return): <c>[T0+5m, open)</c>. By now A2 has long closed, but A4
/// (C3) is <em>still open</em> - it never closes in this scenario either - so the concurrent load
/// counting A3 itself is A1 + A4 + A3 = 3, over capacity: additional too. No reply in this interval at
/// all. <b>This is the scenario's own point worth stating plainly</b>: a naive hand-count that only
/// looks at what was open when C2 first started (A1 + A2 = 2) would call this return interval standard;
/// it is not, because a second, unrelated conversation (C3) that opened in between and never closed is
/// still part of the operator's own load five minutes later. The load is real interval overlap at the
/// instant this interval starts, not a snapshot carried over from the same conversation's earlier
/// interval.</item>
/// </list>
/// <para><b>Ground truth for operator A</b>, against the default bucket bounds <c>[1, 3, 5, 8]</c>
/// (load 1 → bucket "1"; load 2 and load 3 both → bucket "2-3", the bucketing config's own coarseness,
/// not a bug): <see cref="OperatorLoadSummary.ConversationsHeld"/> = 3 (C1, C2, C3 - C2 counted once
/// despite two intervals), <see cref="OperatorLoadSummary.IntervalsHeld"/> = 4,
/// <see cref="OperatorLoadSummary.StandardIntervals"/> = 2 (A1, A2),
/// <see cref="OperatorLoadSummary.AdditionalIntervals"/> = 2 (A4, A3 - the return). Bucket "1": 1
/// interval (A1), 1 reply, average 30s. Bucket "2-3": 3 intervals (A2 at load 2, A4 and A3 both at
/// load 3), 2 replies (A2's 20s, A4's 50s - A3's return has none), average (20+50)/2 = 35s.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class OperatorLoadReportReadStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now =
        new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    private static readonly DateTimeOffset From = Now.AddDays(-1);
    private static readonly DateTimeOffset To = Now.AddDays(1);

    private static readonly AnalyticsOptions Options = new();

    private OperatorLoadReportReadStore Store => new(fixture.DataSource, Options);

    [Fact]
    public async Task GetOperatorLoadReportAsync_AgainstAKnownFixture_MatchesTheHandComputedGroundTruth()
    {
        var (siteId, operatorAId) = await SeedScenarioAsync();

        var result = await Store.GetOperatorLoadReportAsync(siteId, From, To, CancellationToken.None);

        var a = result.Single(s => s.Operator == operatorAId);
        Assert.Equal(3, a.ConversationsHeld);
        Assert.Equal(4, a.IntervalsHeld);
        Assert.Equal(2, a.StandardIntervals);
        Assert.Equal(2, a.AdditionalIntervals);

        Assert.Equal(2, a.ByLoad.Count);
        var bucket1 = a.ByLoad.Single(b => b.BucketLabel == "1");
        Assert.Equal(1, bucket1.IntervalCount);
        Assert.Equal(1, bucket1.ReplyCount);
        AssertClose(30.0, bucket1.AverageFirstReplySeconds);

        var bucket23 = a.ByLoad.Single(b => b.BucketLabel == "2-3");
        Assert.Equal(3, bucket23.IntervalCount);
        Assert.Equal(2, bucket23.ReplyCount);
        AssertClose(35.0, bucket23.AverageFirstReplySeconds);
    }

    /// <summary>`17-01`'s own bar for a new read, applied here: two sites, seeded with deliberately
    /// different numbers, and one site's own report must never surface the other's operator.</summary>
    [Fact]
    public async Task GetOperatorLoadReportAsync_NeverSurfacesAnotherSitesOperator()
    {
        var (siteA, operatorAId) = await SeedScenarioAsync();
        var siteB = new SiteId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteB, $"site_{siteB.Value:N}", []));
            await db.SaveChangesAsync();
        }

        var resultB = await Store.GetOperatorLoadReportAsync(siteB, From, To, CancellationToken.None);
        Assert.Empty(resultB);

        var resultA = await Store.GetOperatorLoadReportAsync(siteA, From, To, CancellationToken.None);
        Assert.Contains(resultA, s => s.Operator == operatorAId);
    }

    [Fact]
    public async Task GetOperatorLoadReportAsync_ForASiteWithNoAssignmentIntervalsInTheWindow_ReturnsEmpty()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            await db.SaveChangesAsync();
        }

        var result = await Store.GetOperatorLoadReportAsync(siteId, From, To, CancellationToken.None);

        Assert.Empty(result);
    }

    private static void AssertClose(double expected, double? actual)
    {
        Assert.NotNull(actual);
        Assert.True(Math.Abs(expected - actual.Value) < 0.01, $"Expected {expected}, got {actual.Value}.");
    }

    private async Task<(SiteId SiteId, OperatorId OperatorAId)> SeedScenarioAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorAId = new OperatorId(Guid.NewGuid());
        var operatorBId = new OperatorId(Guid.NewGuid());
        var t0 = Now;

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Operators.Add(new Operator(operatorAId, siteId, OperatorStatus.Online, capacity: 2));
            db.Operators.Add(new Operator(operatorBId, siteId, OperatorStatus.Online, capacity: 5));
            await db.SaveChangesAsync();
        }

        var c1 = await SeedRepliedConversationAsync(siteId, operatorAId, startedAt: t0, replySeconds: 30);
        await AddAssignmentAsync(siteId, operatorAId, c1, t0, endedAt: null);

        // C2: A holds it, replies at +20s, then transferred to B, then transferred back to A with no
        // further reply.
        var c2Started = t0.AddMinutes(1);
        var c2 = await SeedTransferredConversationAsync(
            siteId, operatorAId, operatorBId, startedAt: c2Started, firstReplySeconds: 20);
        await AddAssignmentAsync(siteId, operatorAId, c2, c2Started, endedAt: c2Started.AddMinutes(2));
        await AddAssignmentAsync(siteId, operatorBId, c2, c2Started.AddMinutes(2), endedAt: c2Started.AddMinutes(4));
        await AddAssignmentAsync(siteId, operatorAId, c2, c2Started.AddMinutes(4), endedAt: null);

        // C3: started while A1 and A2 are both open - additional.
        var c3Started = t0.AddMinutes(2);
        var c3 = await SeedRepliedConversationAsync(siteId, operatorAId, startedAt: c3Started, replySeconds: 50);
        await AddAssignmentAsync(siteId, operatorAId, c3, c3Started, endedAt: null);

        return (siteId, operatorAId);
    }

    /// <summary>A conversation A holds start-to-finish (this test's own scenario never closes it),
    /// with exactly one visitor message and one reply from A, <paramref name="replySeconds"/> later.
    /// </summary>
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

    /// <summary>C2's own conversation aggregate: A replies once, <paramref name="firstReplySeconds"/>
    /// after it starts, then the conversation is handed to B and back to A - the aggregate's own state
    /// tracks the real transfer so <c>AddOperatorMessage</c>'s participant check passes, but this test's
    /// own <c>conversation_assignments</c> rows (added separately by <see cref="AddAssignment"/>,
    /// mirroring <c>ConversationAssignmentOverlapQueryTests</c>' own precedent) are the only thing
    /// <see cref="OperatorLoadReportReadStore"/> actually reads.</summary>
    private async Task<ConversationId> SeedTransferredConversationAsync(
        SiteId siteId, OperatorId operatorAId, OperatorId operatorBId, DateTimeOffset startedAt, int firstReplySeconds)
    {
        var visitorId = new VisitorId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Visitors.Add(new Visitor(visitorId, siteId, startedAt));
            await db.SaveChangesAsync();
        }

        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, startedAt);
        conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hello"), startedAt);
        conversation.AssignTo(operatorAId, startedAt);
        conversation.AddOperatorMessage(
            operatorAId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), startedAt.AddSeconds(firstReplySeconds));
        conversation.TransferTo(operatorBId, startedAt.AddMinutes(2));
        conversation.TransferTo(operatorAId, startedAt.AddMinutes(4));

        await using var writeDb = fixture.CreateDbContext();
        writeDb.Conversations.Add(conversation);
        await writeDb.SaveChangesAsync();
        return conversation.Id;
    }

    private async Task AddAssignmentAsync(
        SiteId siteId, OperatorId operatorId, ConversationId conversationId, DateTimeOffset startedAt, DateTimeOffset? endedAt)
    {
        var interval = ConversationAssignmentInterval.Open(
            new ConversationAssignmentId(Guid.NewGuid()), siteId, conversationId, operatorId,
            ConversationAssignmentSource.Assigned, startedAt);
        if (endedAt is { } end)
        {
            interval.Close(end);
        }

        await using var db = fixture.CreateDbContext();
        db.ConversationAssignments.Add(interval);
        await db.SaveChangesAsync();
    }
}
