using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `18-08`'s own Done-when, against a real Postgres: a real site with a deliberately varied, hand-built
/// set of conversations, and the returned numbers must equal ground truth computed by hand from that
/// plan - not merely "the query runs and returns something the right shape."
///
/// <para><b>The scenario, and the arithmetic every assertion below is derived from.</b> Seven
/// conversations, six inside the report window and one before it:</para>
/// <list type="bullet">
/// <item>#1 Widget: first visitor message, first operator reply 60s later. Answered.</item>
/// <item>#2 Widget: first visitor message, first operator reply 120s later. Answered.</item>
/// <item>#3 Sms: first visitor message, first operator reply 30s later. Answered.</item>
/// <item>#4 Widget: a visitor message, then <c>Close()</c> with no operator message ever - missed.</item>
/// <item>#5 Widget: a visitor message, left <c>Waiting</c> with no reply - <b>not</b> missed, because it
/// is not <c>Closed</c> (`IOperatorAnalyticsReadStore`'s own stated definition).</item>
/// <item>#6 Sms: created <b>before</b> <see cref="From"/> - excluded from every number below entirely,
/// the window test.</item>
/// <item>#7 the channel-tiebreak case: a visitor linked to <em>two</em> channel identities, Sms first
/// (earlier `first_seen_at`) and Max second - the query attributes the conversation to Sms, the
/// earlier-linked one. Answered, 50s.</item>
/// </list>
/// <para>Ground truth: Widget = {#1, #2, #4, #5} - count 4, missed 1, average over {60, 120} = 90s.
/// Sms = {#3, #7} - count 2, missed 0, average over {30, 50} = 40s. Overall = {#1, #2, #3, #4, #5, #7} -
/// count 6, missed 1, average over {60, 120, 30, 50} = 65s. #6 appears nowhere in any of these
/// numbers.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class OperatorAnalyticsReadStoreTests(PostgresFixture fixture)
{
    // Real time, not a fixed date - `2-06` partitions `messages` by `created_at`, and only the
    // current month plus the next two ever have a partition without this test creating one itself
    // (`EnsurePartitionAsync` below covers every month this scenario's own timestamps touch).
    private static readonly DateTimeOffset Now =
        new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    private static readonly DateTimeOffset From = Now.AddDays(-14);
    private static readonly DateTimeOffset To = Now;

    private OperatorAnalyticsReadStore Store => new(fixture.DataSource);

    [Fact]
    public async Task GetSiteAnalyticsAsync_ComputesOverallAndPerChannelNumbers_MatchingHandCalculatedGroundTruth()
    {
        var siteId = await SeedScenarioAsync();

        var result = await Store.GetSiteAnalyticsAsync(siteId, From, To, CancellationToken.None);

        Assert.Equal(6, result.Overall.ConversationCount);
        Assert.Equal(1, result.Overall.MissedCount);
        AssertClose(65.0, result.Overall.AverageFirstResponseSeconds);

        Assert.Equal(2, result.ByChannel.Count);

        var widget = result.ByChannel.Single(c => c.Channel == "Widget");
        Assert.Equal(4, widget.Bucket.ConversationCount);
        Assert.Equal(1, widget.Bucket.MissedCount);
        AssertClose(90.0, widget.Bucket.AverageFirstResponseSeconds);

        var sms = result.ByChannel.Single(c => c.Channel == "Sms");
        Assert.Equal(2, sms.Bucket.ConversationCount);
        Assert.Equal(0, sms.Bucket.MissedCount);
        AssertClose(40.0, sms.Bucket.AverageFirstResponseSeconds);
    }

    /// <summary>#6 alone: created twenty days before <see cref="Now"/>, six days before
    /// <see cref="From"/> - it must not appear in the count, the missed count, or the average, proving
    /// the window is a real filter on <c>conversations.created_at</c> and not merely echoed back on the
    /// response.</summary>
    [Fact]
    public async Task GetSiteAnalyticsAsync_ExcludesConversationsCreatedBeforeTheWindow()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            await db.SaveChangesAsync();
        }

        await SeedOldConversationOutsideWindowAsync(siteId);

        var result = await Store.GetSiteAnalyticsAsync(siteId, From, To, CancellationToken.None);

        Assert.Equal(0, result.Overall.ConversationCount);
        Assert.Empty(result.ByChannel);
    }

    /// <summary>A site with no conversations in the window at all: `GROUPING SETS` over zero input rows
    /// produces zero output rows (the read store's own class doc comment explains why), so this proves
    /// <see cref="OperatorAnalyticsReadStore"/> substitutes the honest zero bucket rather than letting
    /// an empty result read as though the query had failed.</summary>
    [Fact]
    public async Task GetSiteAnalyticsAsync_ForASiteWithNoConversationsInTheWindow_ReturnsZerosAndNulls()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            await db.SaveChangesAsync();
        }

        var result = await Store.GetSiteAnalyticsAsync(siteId, From, To, CancellationToken.None);

        Assert.Equal(0, result.Overall.ConversationCount);
        Assert.Null(result.Overall.AverageFirstResponseSeconds);
        Assert.Equal(0, result.Overall.MissedCount);
        Assert.Empty(result.ByChannel);
    }

    /// <summary>`17-01`'s own bar for a new read: two real sites, seeded with deliberately different
    /// numbers, and asking for one site's analytics must never surface the other's. This is the fails-
    /// before proof's passing half - see this item's own commit-prep notes for the mutation that was
    /// run against this exact test and reverted (the `WHERE site_id = @SiteId` predicate widened to a
    /// tautology) to confirm it actually catches a real leak rather than passing by construction.
    /// </summary>
    [Fact]
    public async Task GetSiteAnalyticsAsync_NeverReturnsAnotherSitesConversations()
    {
        var siteA = await SeedScenarioAsync();
        var siteB = new SiteId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteB, $"site_{siteB.Value:N}", []));
            await db.SaveChangesAsync();
        }
        await SeedSingleAnsweredConversationAsync(siteB, ChannelKind.Telegram, offsetDays: -3, responseSeconds: 10);

        var resultA = await Store.GetSiteAnalyticsAsync(siteA, From, To, CancellationToken.None);
        var resultB = await Store.GetSiteAnalyticsAsync(siteB, From, To, CancellationToken.None);

        // Site A's real ground truth (six conversations) is completely unaffected by Site B's data
        // existing in the same database, and Site B - seeded with exactly one - never sees Site A's
        // six.
        Assert.Equal(6, resultA.Overall.ConversationCount);
        Assert.Equal(1, resultB.Overall.ConversationCount);
        Assert.DoesNotContain(resultB.ByChannel, c => c.Channel == "Widget");
        Assert.DoesNotContain(resultA.ByChannel, c => c.Channel == "Telegram");
    }

    /// <summary>
    /// `18-09`'s own Done-when: real seeded data spanning multiple operators, checked against numbers
    /// worked out by hand, not against "the query looks right." Three conversations, two operators:
    /// </summary>
    /// <list type="bullet">
    /// <item>Operator A answers conversation #A1 in 40s.</item>
    /// <item>Operator A answers conversation #A2 in 80s (average over {40, 80} = 60s).</item>
    /// <item>Operator B answers conversation #B1 in 20s.</item>
    /// </list>
    /// <para>Ground truth: A = {#A1, #A2} - count 2, missed 0, average 60s. B = {#B1} - count 1, missed
    /// 0, average 20s. Neither total includes the other operator's conversations.</para>
    [Fact]
    public async Task GetSiteAnalyticsAsync_ComputesPerOperatorNumbers_MatchingHandCalculatedGroundTruth()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            await db.SaveChangesAsync();
        }

        var operatorA = new OperatorId(Guid.NewGuid());
        var operatorB = new OperatorId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Operators.Add(new Operator(operatorA, siteId, OperatorStatus.Offline, capacity: 5));
            db.Operators.Add(new Operator(operatorB, siteId, OperatorStatus.Offline, capacity: 5));
            await db.SaveChangesAsync();
        }

        await SeedAnsweredByAsync(siteId, operatorA, offsetDays: -5, responseSeconds: 40);
        await SeedAnsweredByAsync(siteId, operatorA, offsetDays: -4, responseSeconds: 80);
        await SeedAnsweredByAsync(siteId, operatorB, offsetDays: -3, responseSeconds: 20);

        var result = await Store.GetSiteAnalyticsAsync(siteId, From, To, CancellationToken.None);

        Assert.Equal(2, result.ByOperator.Count);
        var a = result.ByOperator.Single(o => o.Operator == operatorA);
        Assert.Equal(2, a.Bucket.ConversationCount);
        Assert.Equal(0, a.Bucket.MissedCount);
        AssertClose(60.0, a.Bucket.AverageFirstResponseSeconds);
        var b = result.ByOperator.Single(o => o.Operator == operatorB);
        Assert.Equal(1, b.Bucket.ConversationCount);
        Assert.Equal(0, b.Bucket.MissedCount);
        AssertClose(20.0, b.Bucket.AverageFirstResponseSeconds);
    }

    /// <summary>
    /// `18-09`'s central Done-when: the transfer-attribution decision, proven against a real transfer,
    /// not left implicit. Operator A is assigned, answers once (40s), and the conversation is then
    /// `TransferTo`'d to operator B - who never sends a message at all. <see cref="Conversation.OperatorId"/>
    /// (the currently-assigned operator, `conversations.operator_id`) is B by the time this is saved;
    /// <c>IOperatorAnalyticsReadStore</c>'s own remarks say attribution follows whoever answered first,
    /// never whoever holds the conversation now - so this conversation must land in A's bucket, and B
    /// must not appear in <see cref="OperatorAnalyticsResult.ByOperator"/> at all (B has no conversation
    /// of their own in this scenario, and this transferred one is not one of them either).
    /// </summary>
    [Fact]
    public async Task GetSiteAnalyticsAsync_AttributesATransferredConversation_ToWhoeverAnsweredFirst_NotWhoeverItWasTransferredTo()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            await db.SaveChangesAsync();
        }

        var operatorA = new OperatorId(Guid.NewGuid());
        var operatorB = new OperatorId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Operators.Add(new Operator(operatorA, siteId, OperatorStatus.Offline, capacity: 5));
            db.Operators.Add(new Operator(operatorB, siteId, OperatorStatus.Offline, capacity: 5));
            await db.SaveChangesAsync();
        }

        await EnsurePartitionsAsync([-2]);
        var visitorId = new VisitorId(Guid.NewGuid());
        var createdAt = Now.AddDays(-2);

        await using (var db = fixture.CreateDbContext())
        {
            db.Visitors.Add(new Visitor(visitorId, siteId, createdAt));
            await db.SaveChangesAsync();
        }

        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, createdAt);
        conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), createdAt);
        conversation.AssignTo(operatorA, createdAt);
        conversation.AddOperatorMessage(
            operatorA, new MessageId(Guid.NewGuid()), new MessageBody("hi, this is A"), createdAt.AddSeconds(40));
        // `18-02`: hand the conversation to B without B ever sending a message - the write model's own
        // `operator_id` now points at B, while the answering operator (A) stays fixed in message history.
        conversation.TransferTo(operatorB, createdAt.AddSeconds(50));

        await using var writeDb = fixture.CreateDbContext();
        writeDb.Conversations.Add(conversation);
        await writeDb.SaveChangesAsync();

        var result = await Store.GetSiteAnalyticsAsync(siteId, From, To, CancellationToken.None);

        var operatorBuckets = result.ByOperator.ToDictionary(o => o.Operator);
        Assert.True(operatorBuckets.ContainsKey(operatorA), "The transferred conversation must attribute to A, who answered it.");
        Assert.Equal(1, operatorBuckets[operatorA].Bucket.ConversationCount);
        AssertClose(40.0, operatorBuckets[operatorA].Bucket.AverageFirstResponseSeconds);
        Assert.False(
            operatorBuckets.ContainsKey(operatorB),
            "B never answered anything and only holds this conversation because of the transfer - it must not attribute to B.");
    }

    /// <summary>
    /// `18-09`: the missed-conversation fallback named in <c>IOperatorAnalyticsReadStore</c>'s own
    /// remarks - a conversation nobody ever answered has no "operator who replied," so attribution falls
    /// back to whoever was assigned when it closed. Operator A is assigned, never replies, and the
    /// conversation is closed - `Close()` does not clear `Conversation.OperatorId`, so A is still the
    /// assigned operator of record. This must show up as one of A's conversations and A's one missed
    /// conversation, even though A's `AverageFirstResponseSeconds` stays `null` (nothing to average).
    /// </summary>
    [Fact]
    public async Task GetSiteAnalyticsAsync_AttributesAMissedConversation_ToTheOperatorItWasAssignedToWhenClosed()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            await db.SaveChangesAsync();
        }

        var operatorA = new OperatorId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Operators.Add(new Operator(operatorA, siteId, OperatorStatus.Offline, capacity: 5));
            await db.SaveChangesAsync();
        }

        await EnsurePartitionsAsync([-2]);
        var visitorId = new VisitorId(Guid.NewGuid());
        var createdAt = Now.AddDays(-2);

        await using (var db = fixture.CreateDbContext())
        {
            db.Visitors.Add(new Visitor(visitorId, siteId, createdAt));
            await db.SaveChangesAsync();
        }

        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, createdAt);
        conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("anyone?"), createdAt);
        conversation.AssignTo(operatorA, createdAt);
        conversation.Close(createdAt.AddSeconds(30));

        await using var writeDb = fixture.CreateDbContext();
        writeDb.Conversations.Add(conversation);
        await writeDb.SaveChangesAsync();

        var result = await Store.GetSiteAnalyticsAsync(siteId, From, To, CancellationToken.None);

        var a = result.ByOperator.Single(o => o.Operator == operatorA);
        Assert.Equal(1, a.Bucket.ConversationCount);
        Assert.Equal(1, a.Bucket.MissedCount);
        Assert.Null(a.Bucket.AverageFirstResponseSeconds);
    }

    /// <summary>
    /// `18-13`'s own Done-when, and the one behaviour worth a real fails-before proof: a still-open
    /// conversation must contribute nothing to <c>AverageDurationSeconds</c>, in <em>every</em> bucket
    /// the query already computes - not merely the overall total. Four conversations, two operators, two
    /// channels:
    /// </summary>
    /// <list type="bullet">
    /// <item>D1 Widget/operator A: answered in 10s, closed 100s after it started - duration 100s.</item>
    /// <item>D2 Sms/operator B: answered in 20s, closed 300s after it started - duration 300s.</item>
    /// <item>D3 Widget/operator A: answered in 30s, closed 300s after it started - duration 300s.</item>
    /// <item>D-open Widget/operator A: answered in 15s, never closed - <b>excluded</b> from every
    /// duration average below, though it still counts toward <c>ConversationCount</c> in every bucket it
    /// belongs to (the same "not missed, because it is not Closed" reasoning
    /// <see cref="GetSiteAnalyticsAsync_ComputesOverallAndPerChannelNumbers_MatchingHandCalculatedGroundTruth"/>'s
    /// own #5 already establishes - this item does not change what "missed" means, only adds a duration
    /// number beside it).</item>
    /// </list>
    /// <para>Ground truth, computed by hand: Overall duration average over {100, 300, 300} = 233.33s
    /// (D-open contributes nothing). Widget = {D1, D3, D-open} - count 3, duration average over {100,
    /// 300} = 200s. Sms = {D2} - count 1, duration average 300s. Operator A = {D1, D3, D-open} - count 3,
    /// duration average over {100, 300} = 200s. Operator B = {D2} - count 1, duration average 300s. If
    /// D-open's <c>null</c> <c>ClosedAt</c> were instead treated as zero seconds, Widget's and operator
    /// A's averages would read 133.33s, not 200s - a real, catchable difference, which is what makes this
    /// scenario an actual fails-before proof rather than a query that merely runs.</para>
    [Fact]
    public async Task GetSiteAnalyticsAsync_ComputesAverageDurationSeconds_ExcludingStillOpenConversations_InEveryBucket()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            await db.SaveChangesAsync();
        }

        var operatorA = new OperatorId(Guid.NewGuid());
        var operatorB = new OperatorId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Operators.Add(new Operator(operatorA, siteId, OperatorStatus.Offline, capacity: 5));
            db.Operators.Add(new Operator(operatorB, siteId, OperatorStatus.Offline, capacity: 5));
            await db.SaveChangesAsync();
        }

        // D1: Widget/A, response 10s, closed at +100s.
        await SeedConversationWithDurationAsync(
            siteId, channel: null, operatorA, offsetDays: -5, responseSeconds: 10, closeAfterSeconds: 100);
        // D2: Sms/B, response 20s, closed at +300s.
        await SeedConversationWithDurationAsync(
            siteId, ChannelKind.Sms, operatorB, offsetDays: -4, responseSeconds: 20, closeAfterSeconds: 300);
        // D3: Widget/A, response 30s, closed at +300s.
        await SeedConversationWithDurationAsync(
            siteId, channel: null, operatorA, offsetDays: -3, responseSeconds: 30, closeAfterSeconds: 300);
        // D-open: Widget/A, response 15s, never closed.
        await SeedConversationWithDurationAsync(
            siteId, channel: null, operatorA, offsetDays: -2, responseSeconds: 15, closeAfterSeconds: null);

        var result = await Store.GetSiteAnalyticsAsync(siteId, From, To, CancellationToken.None);

        Assert.Equal(4, result.Overall.ConversationCount);
        AssertClose(233.333333, result.Overall.AverageDurationSeconds!.Value);

        var widget = result.ByChannel.Single(c => c.Channel == "Widget");
        Assert.Equal(3, widget.Bucket.ConversationCount);
        AssertClose(200.0, widget.Bucket.AverageDurationSeconds!.Value);

        var sms = result.ByChannel.Single(c => c.Channel == "Sms");
        Assert.Equal(1, sms.Bucket.ConversationCount);
        AssertClose(300.0, sms.Bucket.AverageDurationSeconds!.Value);

        var a = result.ByOperator.Single(o => o.Operator == operatorA);
        Assert.Equal(3, a.Bucket.ConversationCount);
        AssertClose(200.0, a.Bucket.AverageDurationSeconds!.Value);

        var b = result.ByOperator.Single(o => o.Operator == operatorB);
        Assert.Equal(1, b.Bucket.ConversationCount);
        AssertClose(300.0, b.Bucket.AverageDurationSeconds!.Value);
    }

    /// <summary>Builds one conversation, visitor-message then assigned-to-and-answered-by
    /// <paramref name="operatorId"/>, optionally closed <paramref name="closeAfterSeconds"/> after it
    /// started - <see langword="null"/> leaves it open (`Waiting`/`Assigned`, never `Closed`), the shape
    /// `18-13`'s own still-open-exclusion proof needs and none of the other seed helpers in this file
    /// already give (`SeedSingleAnsweredConversationAsync` never closes; `SeedMissedConversationAsync`
    /// closes but never answers).</summary>
    private async Task SeedConversationWithDurationAsync(
        SiteId siteId, ChannelKind? channel, OperatorId operatorId, int offsetDays, int responseSeconds,
        int? closeAfterSeconds)
    {
        await EnsurePartitionsAsync([offsetDays]);

        var visitorId = new VisitorId(Guid.NewGuid());
        var createdAt = Now.AddDays(offsetDays);

        await using (var db = fixture.CreateDbContext())
        {
            db.Visitors.Add(new Visitor(visitorId, siteId, createdAt));
            if (channel is { } kind)
            {
                db.ChannelIdentities.Add(ChannelIdentity.Link(
                    new ChannelIdentityId(Guid.NewGuid()), siteId, kind,
                    new ExternalChannelAddress($"addr-{Guid.NewGuid():N}"), visitorId, createdAt));
            }

            await db.SaveChangesAsync();
        }

        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, createdAt);
        conversation.AddVisitorMessage(
            visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hello"), createdAt);
        conversation.AssignTo(operatorId, createdAt);
        conversation.AddOperatorMessage(
            operatorId, new MessageId(Guid.NewGuid()), new MessageBody("hi, how can I help"),
            createdAt.AddSeconds(responseSeconds));
        if (closeAfterSeconds is { } seconds)
        {
            conversation.Close(createdAt.AddSeconds(seconds));
        }

        await using var writeDb = fixture.CreateDbContext();
        writeDb.Conversations.Add(conversation);
        await writeDb.SaveChangesAsync();
    }

    /// <summary>`17-01`'s own bar, applied to the per-operator dimension: two sites, each with its own
    /// operator, and one site's analytics must never surface the other site's operator at all - not even
    /// as an empty/zeroed row, since the operator genuinely does not exist in the other site's data.
    /// </summary>
    [Fact]
    public async Task GetSiteAnalyticsAsync_NeverReturnsAnotherSitesOperators()
    {
        var siteA = new SiteId(Guid.NewGuid());
        var siteB = new SiteId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteA, $"site_{siteA.Value:N}", []));
            db.Sites.Add(new Site(siteB, $"site_{siteB.Value:N}", []));
            await db.SaveChangesAsync();
        }

        var operatorA = new OperatorId(Guid.NewGuid());
        var operatorB = new OperatorId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Operators.Add(new Operator(operatorA, siteA, OperatorStatus.Offline, capacity: 5));
            db.Operators.Add(new Operator(operatorB, siteB, OperatorStatus.Offline, capacity: 5));
            await db.SaveChangesAsync();
        }

        await SeedAnsweredByAsync(siteA, operatorA, offsetDays: -2, responseSeconds: 15);
        await SeedAnsweredByAsync(siteB, operatorB, offsetDays: -2, responseSeconds: 25);

        var resultA = await Store.GetSiteAnalyticsAsync(siteA, From, To, CancellationToken.None);
        var resultB = await Store.GetSiteAnalyticsAsync(siteB, From, To, CancellationToken.None);

        Assert.Single(resultA.ByOperator);
        Assert.Equal(operatorA, resultA.ByOperator.Single().Operator);
        Assert.Single(resultB.ByOperator);
        Assert.Equal(operatorB, resultB.ByOperator.Single().Operator);
    }

    /// <summary>
    /// `14-12`/`adr/0079` decision 4: an unlinked <see cref="ChannelIdentity"/> must not keep winning
    /// this tiebreak - proven against a real Postgres, seeding a visitor whose <em>only</em>
    /// <see cref="ChannelIdentity"/> row has already been <see cref="ChannelIdentity.Unlink"/>ed before the
    /// conversation is even started. With the unlinked row excluded, <c>ch</c>'s own lateral join finds
    /// nothing for this visitor, so the conversation must fall back to the honest "Widget" label - the
    /// same fallback a visitor with no channel identity at all already gets - rather than an unlinked
    /// channel silently continuing to win the tiebreak forever.
    /// </summary>
    [Fact]
    public async Task GetSiteAnalyticsAsync_AnUnlinkedChannelIdentity_IsExcludedFromTheChannelTiebreak()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            await db.SaveChangesAsync();
        }

        await EnsurePartitionsAsync([-3]);
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var createdAt = Now.AddDays(-3);

        await using (var db = fixture.CreateDbContext())
        {
            db.Visitors.Add(new Visitor(visitorId, siteId, createdAt));
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Offline, capacity: 5));
            var identity = ChannelIdentity.Link(
                new ChannelIdentityId(Guid.NewGuid()), siteId, ChannelKind.Sms,
                new ExternalChannelAddress($"addr-{Guid.NewGuid():N}"), visitorId, createdAt);
            identity.Unlink(createdAt.AddMinutes(1));
            db.ChannelIdentities.Add(identity);
            await db.SaveChangesAsync();
        }

        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, createdAt);
        conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), createdAt);
        conversation.AssignTo(operatorId, createdAt);
        conversation.AddOperatorMessage(
            operatorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), createdAt.AddSeconds(20));

        await using (var writeDb = fixture.CreateDbContext())
        {
            writeDb.Conversations.Add(conversation);
            await writeDb.SaveChangesAsync();
        }

        var result = await Store.GetSiteAnalyticsAsync(siteId, From, To, CancellationToken.None);

        Assert.Equal(1, result.Overall.ConversationCount);
        var widget = Assert.Single(result.ByChannel);
        Assert.Equal("Widget", widget.Channel);
        Assert.Equal(1, widget.Bucket.ConversationCount);
    }

    /// <summary>Builds one conversation, visitor-message then assigned-to-and-answered-by
    /// <paramref name="operatorId"/> - the shared shape every per-operator test above needs, factored
    /// out once three call sites wanted the identical scenario with only the operator and timing
    /// varying.</summary>
    private async Task SeedAnsweredByAsync(SiteId siteId, OperatorId operatorId, int offsetDays, int responseSeconds)
    {
        await EnsurePartitionsAsync([offsetDays]);

        var visitorId = new VisitorId(Guid.NewGuid());
        var createdAt = Now.AddDays(offsetDays);

        await using (var db = fixture.CreateDbContext())
        {
            db.Visitors.Add(new Visitor(visitorId, siteId, createdAt));
            await db.SaveChangesAsync();
        }

        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, createdAt);
        conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hello"), createdAt);
        conversation.AssignTo(operatorId, createdAt);
        conversation.AddOperatorMessage(
            operatorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), createdAt.AddSeconds(responseSeconds));

        await using var writeDb = fixture.CreateDbContext();
        writeDb.Conversations.Add(conversation);
        await writeDb.SaveChangesAsync();
    }

    private static void AssertClose(double expected, double? actual)
    {
        Assert.NotNull(actual);
        Assert.True(Math.Abs(expected - actual.Value) < 0.01, $"Expected {expected}, got {actual.Value}.");
    }

    private async Task<SiteId> SeedScenarioAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            await db.SaveChangesAsync();
        }

        // #1, #2: Widget - no ChannelIdentity row at all (ChannelKind's own remarks: a widget visitor
        // never has one).
        await SeedSingleAnsweredConversationAsync(siteId, channel: null, offsetDays: -10, responseSeconds: 60);
        await SeedSingleAnsweredConversationAsync(siteId, channel: null, offsetDays: -9, responseSeconds: 120);

        // #3: Sms.
        await SeedSingleAnsweredConversationAsync(siteId, ChannelKind.Sms, offsetDays: -8, responseSeconds: 30);

        // #4: Widget, closed with no operator reply ever - missed.
        await SeedMissedConversationAsync(siteId, offsetDays: -7);

        // #5: Widget, left open (Waiting) with no reply - not missed, still excluded from the average.
        await SeedOpenUnansweredConversationAsync(siteId, offsetDays: -6);

        // #6: Sms, created well before `From` - proven excluded by the dedicated window test, seeded
        // here too so the "no other row leaks into the totals above" claim is real, not merely assumed.
        await SeedOldConversationOutsideWindowAsync(siteId);

        // #7: the channel tiebreak - Sms linked first, Max linked second, so the earlier one wins.
        await SeedChannelTiebreakConversationAsync(siteId, offsetDays: -5, responseSeconds: 50);

        return siteId;
    }

    private async Task SeedSingleAnsweredConversationAsync(
        SiteId siteId, ChannelKind? channel, int offsetDays, int responseSeconds)
    {
        await EnsurePartitionsAsync([offsetDays]);

        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var createdAt = Now.AddDays(offsetDays);

        await using (var db = fixture.CreateDbContext())
        {
            db.Visitors.Add(new Visitor(visitorId, siteId, createdAt));
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Offline, capacity: 5));
            if (channel is { } kind)
            {
                db.ChannelIdentities.Add(ChannelIdentity.Link(
                    new ChannelIdentityId(Guid.NewGuid()), siteId, kind,
                    new ExternalChannelAddress($"addr-{Guid.NewGuid():N}"), visitorId, createdAt));
            }

            await db.SaveChangesAsync();
        }

        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, createdAt);
        conversation.AddVisitorMessage(
            visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hello"), createdAt);
        conversation.AssignTo(operatorId, createdAt);
        conversation.AddOperatorMessage(
            operatorId, new MessageId(Guid.NewGuid()), new MessageBody("hi, how can I help"),
            createdAt.AddSeconds(responseSeconds));

        await using var writeDb = fixture.CreateDbContext();
        writeDb.Conversations.Add(conversation);
        await writeDb.SaveChangesAsync();
    }

    private async Task SeedMissedConversationAsync(SiteId siteId, int offsetDays)
    {
        await EnsurePartitionsAsync([offsetDays]);

        var visitorId = new VisitorId(Guid.NewGuid());
        var createdAt = Now.AddDays(offsetDays);

        await using (var db = fixture.CreateDbContext())
        {
            db.Visitors.Add(new Visitor(visitorId, siteId, createdAt));
            await db.SaveChangesAsync();
        }

        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, createdAt);
        conversation.AddVisitorMessage(
            visitorId, new MessageId(Guid.NewGuid()), new MessageBody("anyone there?"), createdAt);
        conversation.Close(createdAt.AddSeconds(10));

        await using var writeDb = fixture.CreateDbContext();
        writeDb.Conversations.Add(conversation);
        await writeDb.SaveChangesAsync();
    }

    private async Task SeedOpenUnansweredConversationAsync(SiteId siteId, int offsetDays)
    {
        await EnsurePartitionsAsync([offsetDays]);

        var visitorId = new VisitorId(Guid.NewGuid());
        var createdAt = Now.AddDays(offsetDays);

        await using (var db = fixture.CreateDbContext())
        {
            db.Visitors.Add(new Visitor(visitorId, siteId, createdAt));
            await db.SaveChangesAsync();
        }

        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, createdAt);
        conversation.AddVisitorMessage(
            visitorId, new MessageId(Guid.NewGuid()), new MessageBody("still waiting"), createdAt);

        await using var writeDb = fixture.CreateDbContext();
        writeDb.Conversations.Add(conversation);
        await writeDb.SaveChangesAsync();
    }

    private async Task SeedOldConversationOutsideWindowAsync(SiteId siteId)
    {
        await EnsurePartitionsAsync([-20]);

        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var createdAt = Now.AddDays(-20);

        await using (var db = fixture.CreateDbContext())
        {
            db.Visitors.Add(new Visitor(visitorId, siteId, createdAt));
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Offline, capacity: 5));
            db.ChannelIdentities.Add(ChannelIdentity.Link(
                new ChannelIdentityId(Guid.NewGuid()), siteId, ChannelKind.Sms,
                new ExternalChannelAddress($"addr-{Guid.NewGuid():N}"), visitorId, createdAt));
            await db.SaveChangesAsync();
        }

        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, createdAt);
        conversation.AddVisitorMessage(
            visitorId, new MessageId(Guid.NewGuid()), new MessageBody("old message"), createdAt);
        conversation.AssignTo(operatorId, createdAt);
        conversation.AddOperatorMessage(
            operatorId, new MessageId(Guid.NewGuid()), new MessageBody("old reply"), createdAt.AddSeconds(15));

        await using var writeDb = fixture.CreateDbContext();
        writeDb.Conversations.Add(conversation);
        await writeDb.SaveChangesAsync();
    }

    /// <summary>#7: the visitor is linked to two channel identities before the conversation is
    /// started - Sms thirty days ago (the earliest), Max five days ago - so
    /// <see cref="OperatorAnalyticsReadStore"/>'s own "earliest-linked channel wins" tiebreak attributes
    /// the conversation to Sms, never Max.</summary>
    private async Task SeedChannelTiebreakConversationAsync(SiteId siteId, int offsetDays, int responseSeconds)
    {
        await EnsurePartitionsAsync([offsetDays, -30]);

        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var createdAt = Now.AddDays(offsetDays);
        var earlierLink = Now.AddDays(-30);

        await using (var db = fixture.CreateDbContext())
        {
            db.Visitors.Add(new Visitor(visitorId, siteId, earlierLink));
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Offline, capacity: 5));
            db.ChannelIdentities.Add(ChannelIdentity.Link(
                new ChannelIdentityId(Guid.NewGuid()), siteId, ChannelKind.Sms,
                new ExternalChannelAddress($"addr-{Guid.NewGuid():N}"), visitorId, earlierLink));
            db.ChannelIdentities.Add(ChannelIdentity.Link(
                new ChannelIdentityId(Guid.NewGuid()), siteId, ChannelKind.Max,
                new ExternalChannelAddress($"addr-{Guid.NewGuid():N}"), visitorId, createdAt));
            await db.SaveChangesAsync();
        }

        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, createdAt);
        conversation.AddVisitorMessage(
            visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi via sms"), createdAt);
        conversation.AssignTo(operatorId, createdAt);
        conversation.AddOperatorMessage(
            operatorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), createdAt.AddSeconds(responseSeconds));

        await using var writeDb = fixture.CreateDbContext();
        writeDb.Conversations.Add(conversation);
        await writeDb.SaveChangesAsync();
    }

    /// <summary>`13-06`/`adr/0031`: `messages` is now two-level - `PARTITION BY LIST (retention_class)`
    /// at the top, each class itself `PARTITION BY RANGE (created_at)` monthly
    /// (`Stage13RepartitionMessagesByRetentionClass`'s own remarks) - so a monthly leaf is created
    /// under the class-level parent (<see cref="MessagePartitionNames.ForClass"/>), never under
    /// `messages` directly. Every message this test seeds takes the default
    /// <see cref="RetentionClass.Free"/> (`Conversation.AddVisitorMessage`'s own default), so that is
    /// the only class-level parent this helper ever needs. Nothing creates a partition in the past, so
    /// every distinct month this scenario's own timestamps touch must be created up front - the same
    /// insurance `PlatformOverviewFixture.EnsureMessagePartitionsAsync`/
    /// `OwnerSitesEndpointTests.EnsurePartitionAsync` already take for `12-02`'s own older
    /// timestamps, one level deeper now.</summary>
    private async Task EnsurePartitionsAsync(IEnumerable<int> daysAgoValues)
    {
        var classPartition = MessagePartitionNames.ForClass(RetentionClass.Free);
        var months = daysAgoValues
            .Select(daysAgo => Now.AddDays(daysAgo))
            .Select(at => new DateTimeOffset(at.Year, at.Month, 1, 0, 0, 0, TimeSpan.Zero))
            .Distinct();

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        foreach (var from in months)
        {
            var to = from.AddMonths(1);
            var leafName = MessagePartitionNames.ForMonth(RetentionClass.Free, from);
            await using var command = new NpgsqlCommand($"""
                CREATE TABLE IF NOT EXISTS {leafName} PARTITION OF {classPartition}
                    FOR VALUES FROM ('{from:yyyy-MM-dd}') TO ('{to:yyyy-MM-dd}');
                """, connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
