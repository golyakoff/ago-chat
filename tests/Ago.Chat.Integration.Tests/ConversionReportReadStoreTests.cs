using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `18-10`'s own Done-when: real seeded data spanning all three real outcomes plus `Unset`, checked
/// against numbers worked out by hand - and specifically proving `Unset` conversations are excluded
/// from <see cref="ConversionBucket.RecordedCount"/>/<see cref="ConversionBucket.ConversionRate"/>'s
/// denominator, not just that the happy-path numerator is right.
///
/// <para><b>The scenario, and the arithmetic every assertion below is derived from.</b> Six
/// conversations inside the report window, one before it:</para>
/// <list type="bullet">
/// <item>#1, #2, #3: recorded <c>Converted</c>.</item>
/// <item>#4: recorded <c>NotConverted</c>.</item>
/// <item>#5: recorded <c>FollowUpNeeded</c>.</item>
/// <item>#6: never recorded - stays the column's own default, <c>Unset</c>.</item>
/// <item>#7: created <b>before</b> the report window - excluded from every number below entirely.</item>
/// </list>
/// <para>Ground truth: <c>Converted</c> = 3, <c>NotConverted</c> = 1, <c>FollowUpNeeded</c> = 1,
/// <c>Unset</c> = 1, <c>RecordedCount</c> = 3 + 1 = 4, <c>ConversionRate</c> = 3 / 4 = 0.75 - a rate that
/// would read differently (3 / 6 = 0.5, or worse, 3 / 7 with #7 wrongly included) if either the
/// `FollowUpNeeded`/`Unset` exclusion or the window filter were not real.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class ConversionReportReadStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now =
        new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    private static readonly DateTimeOffset From = Now.AddDays(-14);
    private static readonly DateTimeOffset To = Now;

    private ConversionReportReadStore Store => new(fixture.DataSource);

    [Fact]
    public async Task GetConversionReportAsync_ComputesTheRate_ExcludingUnsetAndFollowUpNeededFromTheDenominator()
    {
        var siteId = await SeedScenarioAsync();

        var result = await Store.GetConversionReportAsync(siteId, From, To, CancellationToken.None);

        Assert.Equal(3, result.Overall.ConvertedCount);
        Assert.Equal(1, result.Overall.NotConvertedCount);
        Assert.Equal(1, result.Overall.FollowUpNeededCount);
        Assert.Equal(1, result.Overall.UnsetCount);
        Assert.Equal(4, result.Overall.RecordedCount);
        AssertClose(0.75, result.Overall.ConversionRate);
    }

    /// <summary>The load-bearing half of the scenario above, isolated into its own assertion: swap
    /// out only <c>Unset</c>'s count (seed one more unrecorded conversation) and the rate must not
    /// move at all - if it did, `Unset` would be silently entering the denominator somewhere.</summary>
    [Fact]
    public async Task GetConversionReportAsync_AddingMoreUnsetConversations_NeverMovesTheRate()
    {
        var siteId = await SeedScenarioAsync();
        await SeedConversationAsync(siteId, offsetDays: -1, outcome: null);
        await SeedConversationAsync(siteId, offsetDays: -1, outcome: null);
        await SeedConversationAsync(siteId, offsetDays: -1, outcome: null);

        var result = await Store.GetConversionReportAsync(siteId, From, To, CancellationToken.None);

        Assert.Equal(4, result.Overall.UnsetCount); // the scenario's own #6 plus these three
        Assert.Equal(4, result.Overall.RecordedCount); // unchanged - Converted/NotConverted only
        AssertClose(0.75, result.Overall.ConversionRate);
    }

    [Fact]
    public async Task GetConversionReportAsync_ForASiteWithNoConversationsInTheWindow_ReturnsZerosAndANullRate()
    {
        var siteId = await CreateSiteAsync();

        var result = await Store.GetConversionReportAsync(siteId, From, To, CancellationToken.None);

        Assert.Equal(0, result.Overall.ConvertedCount);
        Assert.Equal(0, result.Overall.NotConvertedCount);
        Assert.Equal(0, result.Overall.FollowUpNeededCount);
        Assert.Equal(0, result.Overall.UnsetCount);
        Assert.Equal(0, result.Overall.RecordedCount);
        Assert.Null(result.Overall.ConversionRate);
        Assert.Empty(result.ByOperator);
    }

    [Fact]
    public async Task GetConversionReportAsync_ExcludesConversationsCreatedBeforeTheWindow()
    {
        var siteId = await CreateSiteAsync();
        await SeedConversationAsync(siteId, offsetDays: -20, outcome: ConversationOutcome.Converted);

        var result = await Store.GetConversionReportAsync(siteId, From, To, CancellationToken.None);

        Assert.Equal(0, result.Overall.ConvertedCount);
        Assert.Equal(0, result.Overall.RecordedCount);
    }

    /// <summary>`17-01`'s own bar for a new read: two real sites, deliberately different numbers, and
    /// asking for one site's report must never surface the other's.</summary>
    [Fact]
    public async Task GetConversionReportAsync_NeverReturnsAnotherSitesConversations()
    {
        var siteA = await SeedScenarioAsync();
        var siteB = await CreateSiteAsync();
        await SeedConversationAsync(siteB, offsetDays: -2, outcome: ConversationOutcome.NotConverted);

        var resultA = await Store.GetConversionReportAsync(siteA, From, To, CancellationToken.None);
        var resultB = await Store.GetConversionReportAsync(siteB, From, To, CancellationToken.None);

        Assert.Equal(3, resultA.Overall.ConvertedCount);
        Assert.Equal(0, resultB.Overall.ConvertedCount);
        Assert.Equal(1, resultB.Overall.NotConvertedCount);
    }

    /// <summary>The per-operator breakdown, extending `18-09`'s own shape - two operators, each with a
    /// deliberately different outcome mix, and neither's numbers may leak into the other's.</summary>
    [Fact]
    public async Task GetConversionReportAsync_ComputesPerOperatorNumbers_MatchingHandCalculatedGroundTruth()
    {
        var siteId = await CreateSiteAsync();
        var operatorA = new OperatorId(Guid.NewGuid());
        var operatorB = new OperatorId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Operators.Add(new Operator(operatorA, siteId, OperatorStatus.Offline, capacity: 5));
            db.Operators.Add(new Operator(operatorB, siteId, OperatorStatus.Offline, capacity: 5));
            await db.SaveChangesAsync();
        }

        // Operator A: two Converted, one NotConverted - rate 2/3.
        await SeedConversationAsync(siteId, offsetDays: -5, outcome: ConversationOutcome.Converted, assignTo: operatorA);
        await SeedConversationAsync(siteId, offsetDays: -4, outcome: ConversationOutcome.Converted, assignTo: operatorA);
        await SeedConversationAsync(siteId, offsetDays: -3, outcome: ConversationOutcome.NotConverted, assignTo: operatorA);
        // Operator B: one Converted, one Unset - rate 1/1, Unset excluded.
        await SeedConversationAsync(siteId, offsetDays: -2, outcome: ConversationOutcome.Converted, assignTo: operatorB);
        await SeedConversationAsync(siteId, offsetDays: -1, outcome: null, assignTo: operatorB);

        var result = await Store.GetConversionReportAsync(siteId, From, To, CancellationToken.None);

        Assert.Equal(2, result.ByOperator.Count);
        var a = result.ByOperator.Single(o => o.Operator == operatorA);
        Assert.Equal(2, a.Bucket.ConvertedCount);
        Assert.Equal(1, a.Bucket.NotConvertedCount);
        Assert.Equal(3, a.Bucket.RecordedCount);
        AssertClose(2.0 / 3.0, a.Bucket.ConversionRate);

        var b = result.ByOperator.Single(o => o.Operator == operatorB);
        Assert.Equal(1, b.Bucket.ConvertedCount);
        Assert.Equal(1, b.Bucket.UnsetCount);
        Assert.Equal(1, b.Bucket.RecordedCount);
        AssertClose(1.0, b.Bucket.ConversionRate);
    }

    /// <summary>`17-01`'s own bar, applied to the per-operator dimension - two sites, each with its own
    /// operator, and one site's report must never surface the other site's operator at all.</summary>
    [Fact]
    public async Task GetConversionReportAsync_NeverReturnsAnotherSitesOperators()
    {
        var siteA = await CreateSiteAsync();
        var siteB = await CreateSiteAsync();
        var operatorA = new OperatorId(Guid.NewGuid());
        var operatorB = new OperatorId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Operators.Add(new Operator(operatorA, siteA, OperatorStatus.Offline, capacity: 5));
            db.Operators.Add(new Operator(operatorB, siteB, OperatorStatus.Offline, capacity: 5));
            await db.SaveChangesAsync();
        }

        await SeedConversationAsync(siteA, offsetDays: -2, outcome: ConversationOutcome.Converted, assignTo: operatorA);
        await SeedConversationAsync(siteB, offsetDays: -2, outcome: ConversationOutcome.NotConverted, assignTo: operatorB);

        var resultA = await Store.GetConversionReportAsync(siteA, From, To, CancellationToken.None);
        var resultB = await Store.GetConversionReportAsync(siteB, From, To, CancellationToken.None);

        Assert.Single(resultA.ByOperator);
        Assert.Equal(operatorA, resultA.ByOperator.Single().Operator);
        Assert.Single(resultB.ByOperator);
        Assert.Equal(operatorB, resultB.ByOperator.Single().Operator);
    }

    /// <summary>An outcome recorded on a conversation nobody was ever assigned to - the `ByOperator`
    /// exclusion `IConversionReportReadStore`'s own remarks state - must still count in
    /// <see cref="ConversionReportResult.Overall"/>, and must not appear in
    /// <see cref="ConversionReportResult.ByOperator"/> at all.</summary>
    [Fact]
    public async Task GetConversionReportAsync_ARecordedOutcomeOnAnUnassignedConversation_CountsOverallOnly()
    {
        var siteId = await CreateSiteAsync();
        await SeedConversationAsync(siteId, offsetDays: -2, outcome: ConversationOutcome.Converted, assignTo: null);

        var result = await Store.GetConversionReportAsync(siteId, From, To, CancellationToken.None);

        Assert.Equal(1, result.Overall.ConvertedCount);
        Assert.Empty(result.ByOperator);
    }

    private static void AssertClose(double expected, double? actual)
    {
        Assert.NotNull(actual);
        Assert.True(Math.Abs(expected - actual.Value) < 0.001, $"Expected {expected}, got {actual.Value}.");
    }

    private async Task<SiteId> CreateSiteAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        await db.SaveChangesAsync();
        return siteId;
    }

    private async Task<SiteId> SeedScenarioAsync()
    {
        var siteId = await CreateSiteAsync();

        await SeedConversationAsync(siteId, offsetDays: -10, outcome: ConversationOutcome.Converted); // #1
        await SeedConversationAsync(siteId, offsetDays: -9, outcome: ConversationOutcome.Converted); // #2
        await SeedConversationAsync(siteId, offsetDays: -8, outcome: ConversationOutcome.Converted); // #3
        await SeedConversationAsync(siteId, offsetDays: -7, outcome: ConversationOutcome.NotConverted); // #4
        await SeedConversationAsync(siteId, offsetDays: -6, outcome: ConversationOutcome.FollowUpNeeded); // #5
        await SeedConversationAsync(siteId, offsetDays: -5, outcome: null); // #6 - stays Unset
        await SeedConversationAsync(siteId, offsetDays: -20, outcome: ConversationOutcome.Converted); // #7 - before the window

        return siteId;
    }

    /// <summary>No messages, no partitions to pre-create (unlike `OperatorAnalyticsReadStoreTests`'
    /// own seeding) - an outcome lives on `conversations` directly, `IConversionReportReadStore`'s own
    /// remarks on why this query needs none of that store's message-history joins.</summary>
    private async Task SeedConversationAsync(
        SiteId siteId, int offsetDays, ConversationOutcome? outcome, OperatorId? assignTo = null)
    {
        var visitorId = new VisitorId(Guid.NewGuid());
        var createdAt = Now.AddDays(offsetDays);

        await using (var db = fixture.CreateDbContext())
        {
            db.Visitors.Add(new Visitor(visitorId, siteId, createdAt));
            await db.SaveChangesAsync();
        }

        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, createdAt);
        if (assignTo is { } operatorId)
        {
            conversation.AssignTo(operatorId, createdAt);
        }

        if (outcome is { } realOutcome)
        {
            conversation.SetOutcome(realOutcome);
        }

        await using var writeDb = fixture.CreateDbContext();
        writeDb.Conversations.Add(conversation);
        await writeDb.SaveChangesAsync();
    }
}
