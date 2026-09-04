using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `18-11`'s own Done-when: real seeded data with a mix of tagged, untagged and multi-tagged
/// conversations, checked against numbers worked out by hand - proving both the per-tag counting rule
/// (once per tag a conversation holds) and the site-wide "percentage tagged" honesty figure.
///
/// <para><b>The scenario, and the arithmetic every assertion below is derived from.</b> Five
/// conversations inside the report window, one before it:</para>
/// <list type="bullet">
/// <item>#1: tagged <c>Billing</c> only (operator-applied). Outcome <c>Converted</c>.</item>
/// <item>#2: tagged both <c>Billing</c> and <c>Shipping</c> - the multi-tag case. Outcome
/// <c>NotConverted</c>.</item>
/// <item>#3: tagged <c>Shipping</c> only, this time AI-applied (`19-02`'s own `TagSource.Ai`) - proving
/// the breakdown counts a tag regardless of who/what applied it. Outcome left <c>Unset</c>.</item>
/// <item>#4: untagged. Outcome <c>Converted</c>.</item>
/// <item>#5: untagged, outcome left <c>Unset</c>.</item>
/// <item>#6: tagged <c>Billing</c>, created <b>before</b> the report window - excluded from every number
/// below entirely.</item>
/// </list>
/// <para>Ground truth: <c>TotalConversationCount</c> = 5 (#1-#5), <c>TaggedConversationCount</c> = 3
/// (#1, #2, #3 - counted once each, even though #2 carries two tags), <c>PercentageTagged</c> = 3 / 5 =
/// 0.6. <c>Billing</c>'s own bucket: #1 and #2 -&gt; <c>ConversationCount</c> = 2, one <c>Converted</c>
/// (#1) and one <c>NotConverted</c> (#2) -&gt; rate 1/2 = 0.5. <c>Shipping</c>'s own bucket: #2 and #3
/// -&gt; <c>ConversationCount</c> = 2, one <c>NotConverted</c> (#2) and #3's own <c>Unset</c> excluded
/// from the denominator -&gt; rate 0/1 = 0.0. The two buckets' own counts (2 + 2 = 4) deliberately do not
/// equal <c>TaggedConversationCount</c> (3) - conversation #2 is counted in both, once per tag it holds,
/// exactly the rule this item's own Done-when states explicitly.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class TagBreakdownReadStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now =
        new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    private static readonly DateTimeOffset From = Now.AddDays(-14);
    private static readonly DateTimeOffset To = Now;

    private TagBreakdownReadStore Store => new(fixture.DataSource, new AnalyticsOptions { MinimumSampleForRate = 10 });

    /// <summary>`23-16`: a store built with a caller-chosen threshold, for the ordering test below - the
    /// same reason `ConversionReportReadStoreTests.StoreWithThreshold` exists.</summary>
    private TagBreakdownReadStore StoreWithThreshold(int minimumSampleForRate) =>
        new(fixture.DataSource, new AnalyticsOptions { MinimumSampleForRate = minimumSampleForRate });

    [Fact]
    public async Task GetTagBreakdownAsync_ComputesCoverageAndPerTagNumbers_MatchingHandCalculatedGroundTruth()
    {
        var siteId = await SeedScenarioAsync();

        var result = await Store.GetTagBreakdownAsync(siteId, From, To, CancellationToken.None);

        Assert.Equal(5, result.TotalConversationCount);
        Assert.Equal(3, result.TaggedConversationCount);
        AssertClose(0.6, result.PercentageTagged);

        Assert.Equal(2, result.ByTag.Count);
        var billing = result.ByTag.Single(t => t.TagName == "Billing");
        Assert.Equal(2, billing.ConversationCount);
        Assert.Equal(1, billing.ConvertedCount);
        Assert.Equal(1, billing.NotConvertedCount);
        Assert.Equal(2, billing.RecordedCount);
        AssertClose(0.5, billing.ConversionRate);

        var shipping = result.ByTag.Single(t => t.TagName == "Shipping");
        Assert.Equal(2, shipping.ConversationCount);
        Assert.Equal(0, shipping.ConvertedCount);
        Assert.Equal(1, shipping.NotConvertedCount);
        Assert.Equal(1, shipping.RecordedCount);
        AssertClose(0.0, shipping.ConversionRate);
    }

    /// <summary>The load-bearing assertion this item's own Done-when names explicitly: a conversation
    /// holding two tags must not make the per-tag counts sum back up to the site-wide total - each tag's
    /// own bucket counts that conversation in full, independently.</summary>
    [Fact]
    public async Task GetTagBreakdownAsync_AConversationWithMultipleTags_CountsOncePerTag_NotOnceOverall()
    {
        var siteId = await SeedScenarioAsync();

        var result = await Store.GetTagBreakdownAsync(siteId, From, To, CancellationToken.None);

        var sumOfPerTagCounts = result.ByTag.Sum(t => t.ConversationCount);
        Assert.Equal(4, sumOfPerTagCounts); // Billing(2) + Shipping(2)
        Assert.NotEqual(result.TaggedConversationCount, sumOfPerTagCounts); // 3 tagged conversations, not 4
    }

    [Fact]
    public async Task GetTagBreakdownAsync_ForASiteWithNoConversationsInTheWindow_ReturnsZerosAndANullPercentage()
    {
        var siteId = await CreateSiteAsync();

        var result = await Store.GetTagBreakdownAsync(siteId, From, To, CancellationToken.None);

        Assert.Equal(0, result.TotalConversationCount);
        Assert.Equal(0, result.TaggedConversationCount);
        Assert.Null(result.PercentageTagged);
        Assert.Empty(result.ByTag);
    }

    [Fact]
    public async Task GetTagBreakdownAsync_ExcludesConversationsCreatedBeforeTheWindow()
    {
        var siteId = await CreateSiteAsync();
        var tagId = await SeedTagAsync(siteId, "Billing");
        await SeedConversationAsync(siteId, offsetDays: -20, outcome: ConversationOutcome.Converted, tags: [(tagId, TagSource.Operator)]);

        var result = await Store.GetTagBreakdownAsync(siteId, From, To, CancellationToken.None);

        Assert.Equal(0, result.TotalConversationCount);
        Assert.Equal(0, result.TaggedConversationCount);
        Assert.Empty(result.ByTag);
    }

    /// <summary>`17-01`'s own bar for a new read: two real sites, deliberately different tags and
    /// numbers, and asking for one site's report must never surface the other's - neither its
    /// conversations nor its tag vocabulary.</summary>
    [Fact]
    public async Task GetTagBreakdownAsync_NeverReturnsAnotherSitesConversationsOrTags()
    {
        var siteA = await SeedScenarioAsync();
        var siteB = await CreateSiteAsync();
        var vipTagId = await SeedTagAsync(siteB, "VIP");
        await SeedConversationAsync(siteB, offsetDays: -2, outcome: ConversationOutcome.Converted, tags: [(vipTagId, TagSource.Operator)]);

        var resultA = await Store.GetTagBreakdownAsync(siteA, From, To, CancellationToken.None);
        var resultB = await Store.GetTagBreakdownAsync(siteB, From, To, CancellationToken.None);

        Assert.DoesNotContain(resultA.ByTag, t => t.TagName == "VIP");
        Assert.Equal(2, resultA.ByTag.Count); // Billing, Shipping only

        Assert.Single(resultB.ByTag);
        Assert.Equal("VIP", resultB.ByTag.Single().TagName);
        Assert.Equal(1, resultB.TotalConversationCount);
        Assert.Equal(1, resultB.TaggedConversationCount);
    }

    /// <summary>`19-02`'s own addition: a tag applied by the AI categorizer counts in the breakdown and
    /// in the coverage figure exactly the same as an operator-applied one - the breakdown reports on
    /// whatever tags exist, "by whatever means they were applied" (this item's own Out-of-scope wording).
    /// </summary>
    [Fact]
    public async Task GetTagBreakdownAsync_CountsAiAppliedTagsTheSameAsOperatorAppliedOnes()
    {
        var siteId = await CreateSiteAsync();
        var tagId = await SeedTagAsync(siteId, "Billing");
        await SeedConversationAsync(siteId, offsetDays: -1, outcome: null, tags: [(tagId, TagSource.Ai)]);

        var result = await Store.GetTagBreakdownAsync(siteId, From, To, CancellationToken.None);

        Assert.Equal(1, result.TaggedConversationCount);
        Assert.Equal(1, result.ByTag.Single().ConversationCount);
    }

    /// <summary>`23-16`'s own load-bearing proof for this report, the identical shape
    /// `ConversionReportReadStoreTests.GetConversionReportAsync_NeverRanksAThinSampleAboveARealRate_EvenWhenItsOwnRateIsHigher`
    /// already establishes: a tag with a thin sample and a perfect rate must never outrank a tag whose
    /// own sample clears the configured threshold, even at a worse rate.
    ///
    /// <para>Threshold set to 4. "Popular" carries 4 recorded outcomes (meets threshold), rate 0.25 (1
    /// converted of 4). "RareA" carries 2 recorded outcomes (below threshold), rate 1.0. "RareB" carries
    /// 1 recorded outcome (below threshold), rate 1.0. Expected order: Popular first (alone in the
    /// meets-threshold group); RareA before RareB among the below-threshold group, ranked by
    /// `ConversationCount` (2 vs 1) rather than by their tied 100% rate.</para>
    /// </summary>
    [Fact]
    public async Task GetTagBreakdownAsync_NeverRanksAThinSampleAboveARealRate_EvenWhenItsOwnRateIsHigher()
    {
        var siteId = await CreateSiteAsync();
        var popularTagId = await SeedTagAsync(siteId, "Popular");
        var rareATagId = await SeedTagAsync(siteId, "RareA");
        var rareBTagId = await SeedTagAsync(siteId, "RareB");

        // Popular: 4 recorded (meets threshold=4) - 1 Converted, 3 NotConverted - rate 0.25.
        await SeedConversationAsync(siteId, offsetDays: -10, outcome: ConversationOutcome.Converted, tags: [(popularTagId, TagSource.Operator)]);
        await SeedConversationAsync(siteId, offsetDays: -9, outcome: ConversationOutcome.NotConverted, tags: [(popularTagId, TagSource.Operator)]);
        await SeedConversationAsync(siteId, offsetDays: -8, outcome: ConversationOutcome.NotConverted, tags: [(popularTagId, TagSource.Operator)]);
        await SeedConversationAsync(siteId, offsetDays: -7, outcome: ConversationOutcome.NotConverted, tags: [(popularTagId, TagSource.Operator)]);
        // RareA: 2 recorded (below threshold) - both Converted - rate 1.0.
        await SeedConversationAsync(siteId, offsetDays: -6, outcome: ConversationOutcome.Converted, tags: [(rareATagId, TagSource.Operator)]);
        await SeedConversationAsync(siteId, offsetDays: -5, outcome: ConversationOutcome.Converted, tags: [(rareATagId, TagSource.Operator)]);
        // RareB: 1 recorded (below threshold) - Converted - rate 1.0.
        await SeedConversationAsync(siteId, offsetDays: -4, outcome: ConversationOutcome.Converted, tags: [(rareBTagId, TagSource.Operator)]);

        var result = await StoreWithThreshold(4).GetTagBreakdownAsync(siteId, From, To, CancellationToken.None);

        Assert.Equal(3, result.ByTag.Count);
        Assert.Equal(["Popular", "RareA", "RareB"], result.ByTag.Select(t => t.TagName));
        AssertClose(0.25, result.ByTag[0].ConversionRate);
        AssertClose(1.0, result.ByTag[1].ConversionRate);
        AssertClose(1.0, result.ByTag[2].ConversionRate);
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

    private async Task<TagId> SeedTagAsync(SiteId siteId, string name)
    {
        var tagId = new TagId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Tags.Add(Tag.Create(tagId, siteId, name, Now));
        await db.SaveChangesAsync();
        return tagId;
    }

    private async Task<SiteId> SeedScenarioAsync()
    {
        var siteId = await CreateSiteAsync();
        var billingTagId = await SeedTagAsync(siteId, "Billing");
        var shippingTagId = await SeedTagAsync(siteId, "Shipping");

        // #1: Billing only, Converted.
        await SeedConversationAsync(siteId, offsetDays: -10, outcome: ConversationOutcome.Converted, tags: [(billingTagId, TagSource.Operator)]);
        // #2: Billing + Shipping (multi-tag), NotConverted.
        await SeedConversationAsync(
            siteId, offsetDays: -9, outcome: ConversationOutcome.NotConverted,
            tags: [(billingTagId, TagSource.Operator), (shippingTagId, TagSource.Operator)]);
        // #3: Shipping only, AI-applied, Unset.
        await SeedConversationAsync(siteId, offsetDays: -8, outcome: null, tags: [(shippingTagId, TagSource.Ai)]);
        // #4: untagged, Converted.
        await SeedConversationAsync(siteId, offsetDays: -7, outcome: ConversationOutcome.Converted, tags: []);
        // #5: untagged, Unset.
        await SeedConversationAsync(siteId, offsetDays: -6, outcome: null, tags: []);
        // #6: before the window - excluded entirely.
        await SeedConversationAsync(siteId, offsetDays: -20, outcome: null, tags: [(billingTagId, TagSource.Operator)]);

        return siteId;
    }

    /// <summary>No messages, no partitions to pre-create - the identical "an outcome lives directly on
    /// `conversations`" shape `ConversionReportReadStoreTests`'s own seeding already establishes, plus a
    /// tag list applied afterward through the real write path (`ITagRepository.AddToConversationAsync`),
    /// the same real-domain-path bar `ConversationCategorizationJobTests` already sets for `19-02`.
    /// </summary>
    private async Task SeedConversationAsync(
        SiteId siteId, int offsetDays, ConversationOutcome? outcome, IReadOnlyList<(TagId TagId, TagSource Source)> tags)
    {
        var visitorId = new VisitorId(Guid.NewGuid());
        var createdAt = Now.AddDays(offsetDays);

        await using (var db = fixture.CreateDbContext())
        {
            db.Visitors.Add(new Visitor(visitorId, siteId, createdAt));
            await db.SaveChangesAsync();
        }

        var conversationId = new ConversationId(Guid.NewGuid());
        var conversation = Conversation.Start(conversationId, siteId, visitorId, createdAt);
        if (outcome is { } realOutcome)
        {
            conversation.SetOutcome(realOutcome);
        }

        await using (var writeDb = fixture.CreateDbContext())
        {
            writeDb.Conversations.Add(conversation);
            await writeDb.SaveChangesAsync();
        }

        var tagRepository = new TagRepository(fixture.CreateDbContext());
        foreach (var (tagId, source) in tags)
        {
            await tagRepository.AddToConversationAsync(conversationId, tagId, source, CancellationToken.None);
        }
    }
}
