using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetConversionReportForSite;
using Ago.Chat.Application.UseCases.GetOperatorAnalyticsForSite;
using Ago.Chat.Application.UseCases.GetOwnAnalyticsForOperator;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetOwnAnalyticsForOperator;

public class GetOwnAnalyticsForOperatorHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly SiteId OtherSiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorA = new(Guid.NewGuid());
    private static readonly OperatorId OperatorB = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static (
        GetOwnAnalyticsForOperatorHandler Handler,
        FakeOperatorAnalyticsReadStore AnalyticsStore,
        FakeOperatorLoadReportReadStore LoadStore,
        FakeConversionReportReadStore ConversionStore) CreateFixture()
    {
        var analyticsStore = new FakeOperatorAnalyticsReadStore();
        var loadStore = new FakeOperatorLoadReportReadStore();
        var conversionStore = new FakeConversionReportReadStore();
        var clock = new FakeClock(Now);
        return (
            new GetOwnAnalyticsForOperatorHandler(analyticsStore, loadStore, conversionStore, clock),
            analyticsStore, loadStore, conversionStore);
    }

    [Fact]
    public async Task HandleAsync_WhenFromIsNotBeforeTo_ReturnsAnalyticsInvalidRange()
    {
        var (handler, _, _, _) = CreateFixture();

        var result = await handler.HandleAsync(
            new Application.UseCases.GetOwnAnalyticsForOperator.GetOwnAnalyticsForOperator(OperatorA, SiteId, Now, Now.AddDays(-1)), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Analytics.InvalidRange", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenFromEqualsTo_ReturnsAnalyticsInvalidRange()
    {
        var (handler, _, _, _) = CreateFixture();

        var result = await handler.HandleAsync(
            new Application.UseCases.GetOwnAnalyticsForOperator.GetOwnAnalyticsForOperator(OperatorA, SiteId, Now, Now), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Analytics.InvalidRange", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenNoRangeIsSupplied_DefaultsToTheTrailingWindow_AndEchoesItBack()
    {
        var (handler, analyticsStore, _, _) = CreateFixture();

        var result = await handler.HandleAsync(
            new Application.UseCases.GetOwnAnalyticsForOperator.GetOwnAnalyticsForOperator(OperatorA, SiteId, null, null), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var expectedFrom = Now.AddDays(-GetOwnAnalyticsForOperatorHandler.DefaultWindowDays);
        Assert.Equal(expectedFrom, result.Value.From);
        Assert.Equal(Now, result.Value.To);
        Assert.Equal(expectedFrom, analyticsStore.Calls[0].From);
        Assert.Equal(Now, analyticsStore.Calls[0].To);
    }

    [Fact]
    public async Task HandleAsync_WhenARangeIsSupplied_PassesItThroughUnchanged()
    {
        var (handler, analyticsStore, _, _) = CreateFixture();
        var from = Now.AddDays(-10);
        var to = Now.AddDays(-1);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetOwnAnalyticsForOperator.GetOwnAnalyticsForOperator(OperatorA, SiteId, from, to), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(from, result.Value.From);
        Assert.Equal(to, result.Value.To);
        Assert.Equal(from, analyticsStore.Calls[0].From);
        Assert.Equal(to, analyticsStore.Calls[0].To);
    }

    /// <summary>The tenant-isolation half of Done-when. There is no operator-id parameter anywhere on
    /// the <c>GetOwnAnalyticsForOperator</c> query for a caller to name another site through - the site
    /// comes from <c>query.SiteId</c> alone, which the endpoint reads from the same validated token as
    /// <c>RequestedBy</c> (`ConversationsEndpoints.HandleGetOwnAnalyticsAsync`). What is left to prove at
    /// this layer is that the handler actually forwards that site to every read store it calls, never a
    /// hardcoded or substituted one - the same shape
    /// `GetOperatorAnalyticsForSiteHandlerTests.HandleAsync_PassesTheCallersOwnSiteId_NeverAnother`
    /// already proves for the tenant-wide report these stores also serve.</summary>
    [Fact]
    public async Task HandleAsync_PassesTheCallersOwnSiteId_NeverAnother_ToEveryStore()
    {
        var (handler, analyticsStore, loadStore, conversionStore) = CreateFixture();

        await handler.HandleAsync(new Application.UseCases.GetOwnAnalyticsForOperator.GetOwnAnalyticsForOperator(OperatorA, SiteId, null, null), CancellationToken.None);

        Assert.Equal(SiteId, analyticsStore.LastSiteId);
        Assert.NotEqual(OtherSiteId, analyticsStore.LastSiteId);
        Assert.Equal(SiteId, loadStore.Calls[^1].SiteId);
        Assert.NotEqual(OtherSiteId, loadStore.Calls[^1].SiteId);
        Assert.Equal(SiteId, conversionStore.LastSiteId);
        Assert.NotEqual(OtherSiteId, conversionStore.LastSiteId);
    }

    /// <summary><b>The single most important test in this file.</b> `GetOwnAnalyticsForOperator` has
    /// exactly one identifier on it, <c>RequestedBy</c>, which is both
    /// "who is asking" and "whose row comes back" - there is no second, operator-scoping parameter for a
    /// caller to substitute another operator's id into (`GetOwnAnalyticsForOperator`'s own remarks). This
    /// proves that claim behaviourally rather than merely by the record's shape: with both operators'
    /// data seeded in the same site, asking as A returns only A's figures, and asking as B (same store,
    /// same window, same site) returns only B's - never the other one's, and never both merged.</summary>
    [Fact]
    public async Task HandleAsync_ReturnsOnlyTheCallersOwnRow_NeverAnotherOperatorsInTheSameSite()
    {
        var (handler, analyticsStore, loadStore, conversionStore) = CreateFixture();
        analyticsStore.Seed(new OperatorAnalyticsResult(
            new OperatorAnalyticsBucket(5, 30.0, 200.0, 1),
            [],
            [
                new OperatorAnalyticsOperatorBucket(OperatorA, new OperatorAnalyticsBucket(3, 20.0, 180.0, 0), "Ada"),
                new OperatorAnalyticsOperatorBucket(OperatorB, new OperatorAnalyticsBucket(2, 50.0, 240.0, 1), "Grace"),
            ],
            [],
            []));
        loadStore.Seed([
            new OperatorLoadSummary(OperatorA, "Ada", 3, 3, 3, 0, [new OperatorLoadBucketEntry("1", 3, 3, 20.0)]),
            new OperatorLoadSummary(OperatorB, "Grace", 2, 3, 1, 2, [new OperatorLoadBucketEntry("2-3", 3, 2, 50.0)]),
        ]);
        conversionStore.Seed(new ConversionReportResult(
            new ConversionBucket(3, 2, 0, 0, 5, 0.6),
            [
                new ConversionOperatorBucket(OperatorA, new ConversionBucket(2, 1, 0, 0, 3, 2.0 / 3), "Ada"),
                new ConversionOperatorBucket(OperatorB, new ConversionBucket(1, 1, 0, 0, 2, 0.5), "Grace"),
            ]));

        var resultForA = await handler.HandleAsync(
            new Application.UseCases.GetOwnAnalyticsForOperator.GetOwnAnalyticsForOperator(OperatorA, SiteId, null, null), CancellationToken.None);
        var resultForB = await handler.HandleAsync(
            new Application.UseCases.GetOwnAnalyticsForOperator.GetOwnAnalyticsForOperator(OperatorB, SiteId, null, null), CancellationToken.None);

        Assert.True(resultForA.IsSuccess);
        Assert.Equal(3, resultForA.Value.Bucket.ConversationCount);
        Assert.Equal(20.0, resultForA.Value.Bucket.AverageFirstResponseSeconds);
        Assert.NotNull(resultForA.Value.Load);
        Assert.Equal(3, resultForA.Value.Load!.StandardIntervals);
        Assert.Equal(0, resultForA.Value.Load.AdditionalIntervals);
        Assert.NotNull(resultForA.Value.Conversion);
        Assert.Equal(2, resultForA.Value.Conversion!.ConvertedCount);

        Assert.True(resultForB.IsSuccess);
        Assert.Equal(2, resultForB.Value.Bucket.ConversationCount);
        Assert.Equal(50.0, resultForB.Value.Bucket.AverageFirstResponseSeconds);
        Assert.NotNull(resultForB.Value.Load);
        Assert.Equal(1, resultForB.Value.Load!.StandardIntervals);
        Assert.Equal(2, resultForB.Value.Load.AdditionalIntervals);
        Assert.NotNull(resultForB.Value.Conversion);
        Assert.Equal(1, resultForB.Value.Conversion!.ConvertedCount);

        // Neither row leaks a fragment of the other's numbers.
        Assert.NotEqual(resultForA.Value.Bucket.ConversationCount, resultForB.Value.Bucket.ConversationCount);
        Assert.NotEqual(resultForA.Value.Load!.AdditionalIntervals, resultForB.Value.Load!.AdditionalIntervals);
    }

    /// <summary><b>The other mandatory assertion: an operator's own row equals their row in the
    /// tenant's own report, over the same range.</b> This seeds the identical fakes and drives both
    /// <see cref="GetOperatorAnalyticsForSiteHandler"/> (as an admin, `site:configure` granted) and
    /// <see cref="GetOwnAnalyticsForOperatorHandler"/> (as the operator named in the row) against the
    /// same data, then compares field by field - never eyeballed. Both handlers reach the merge through
    /// the same internal <c>OperatorAnalyticsMerge.ComposeByOperator</c>, so a real behavioural
    /// difference here would mean the shared merge itself disagreed with what it just returned, which is
    /// not possible;
    /// this test's real job is to catch a *future* change that adds a second, divergent code path
    /// instead of reusing the shared one.</summary>
    [Fact]
    public async Task HandleAsync_OwnAnalyticsRow_EqualsTheOperatorsOwnRowInTheTenantReport()
    {
        var analyticsStore = new FakeOperatorAnalyticsReadStore();
        var loadStore = new FakeOperatorLoadReportReadStore();
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorA, SiteId, Permission.SiteConfigure);
        var clock = new FakeClock(Now);

        analyticsStore.Seed(new OperatorAnalyticsResult(
            new OperatorAnalyticsBucket(4, 25.0, 210.0, 1),
            [],
            [new OperatorAnalyticsOperatorBucket(OperatorB, new OperatorAnalyticsBucket(4, 25.0, 210.0, 1), "Grace")],
            [],
            []));
        loadStore.Seed([
            new OperatorLoadSummary(OperatorB, "Grace", 4, 5, 3, 2, [new OperatorLoadBucketEntry("2-3", 5, 4, 25.0)]),
        ]);

        var tenantReportHandler = new GetOperatorAnalyticsForSiteHandler(analyticsStore, loadStore, permissions, clock);
        var ownReportHandler = new GetOwnAnalyticsForOperatorHandler(
            analyticsStore, loadStore, new FakeConversionReportReadStore(), clock);

        var tenantReport = await tenantReportHandler.HandleAsync(
            new Application.UseCases.GetOperatorAnalyticsForSite.GetOperatorAnalyticsForSite(OperatorA, SiteId, null, null),
            CancellationToken.None);
        var ownReport = await ownReportHandler.HandleAsync(
            new Application.UseCases.GetOwnAnalyticsForOperator.GetOwnAnalyticsForOperator(OperatorB, SiteId, null, null), CancellationToken.None);

        Assert.True(tenantReport.IsSuccess);
        Assert.True(ownReport.IsSuccess);
        var tenantRow = tenantReport.Value.ByOperator.Single(o => o.OperatorId == OperatorB.Value);

        Assert.Equal(tenantRow.Bucket.ConversationCount, ownReport.Value.Bucket.ConversationCount);
        Assert.Equal(tenantRow.Bucket.AverageFirstResponseSeconds, ownReport.Value.Bucket.AverageFirstResponseSeconds);
        Assert.Equal(tenantRow.Bucket.AverageDurationSeconds, ownReport.Value.Bucket.AverageDurationSeconds);
        Assert.Equal(tenantRow.Bucket.MissedCount, ownReport.Value.Bucket.MissedCount);
        Assert.NotNull(tenantRow.Load);
        Assert.NotNull(ownReport.Value.Load);
        Assert.Equal(tenantRow.Load!.ConversationsHeld, ownReport.Value.Load!.ConversationsHeld);
        Assert.Equal(tenantRow.Load.IntervalsHeld, ownReport.Value.Load.IntervalsHeld);
        Assert.Equal(tenantRow.Load.StandardIntervals, ownReport.Value.Load.StandardIntervals);
        Assert.Equal(tenantRow.Load.AdditionalIntervals, ownReport.Value.Load.AdditionalIntervals);
    }

    /// <summary>The conversion half of the same equality claim, against
    /// <see cref="GetConversionReportForSiteHandler"/> - `GetOwnAnalyticsForOperatorHandler`'s own
    /// remarks on why this needs its own fake rather than reusing the analytics one above (a genuinely
    /// different store, `IConversionReportReadStore`).</summary>
    [Fact]
    public async Task HandleAsync_OwnConversionFigures_EqualTheOperatorsOwnRowInTheConversionReport()
    {
        var conversionStore = new FakeConversionReportReadStore();
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorA, SiteId, Permission.SiteConfigure);
        var clock = new FakeClock(Now);

        conversionStore.Seed(new ConversionReportResult(
            new ConversionBucket(3, 1, 1, 2, 4, 0.75),
            [new ConversionOperatorBucket(OperatorB, new ConversionBucket(3, 1, 1, 2, 4, 0.75), "Grace")]));

        var tenantReportHandler = new GetConversionReportForSiteHandler(conversionStore, permissions, clock);
        var ownReportHandler = new GetOwnAnalyticsForOperatorHandler(
            new FakeOperatorAnalyticsReadStore(), new FakeOperatorLoadReportReadStore(), conversionStore, clock);

        var tenantReport = await tenantReportHandler.HandleAsync(
            new Application.UseCases.GetConversionReportForSite.GetConversionReportForSite(OperatorA, SiteId, null, null),
            CancellationToken.None);
        var ownReport = await ownReportHandler.HandleAsync(
            new Application.UseCases.GetOwnAnalyticsForOperator.GetOwnAnalyticsForOperator(OperatorB, SiteId, null, null), CancellationToken.None);

        Assert.True(tenantReport.IsSuccess);
        Assert.True(ownReport.IsSuccess);
        var tenantRow = tenantReport.Value.ByOperator.Single(o => o.OperatorId == OperatorB.Value);

        Assert.NotNull(ownReport.Value.Conversion);
        Assert.Equal(tenantRow.Bucket.ConvertedCount, ownReport.Value.Conversion!.ConvertedCount);
        Assert.Equal(tenantRow.Bucket.NotConvertedCount, ownReport.Value.Conversion.NotConvertedCount);
        Assert.Equal(tenantRow.Bucket.FollowUpNeededCount, ownReport.Value.Conversion.FollowUpNeededCount);
        Assert.Equal(tenantRow.Bucket.UnsetCount, ownReport.Value.Conversion.UnsetCount);
        Assert.Equal(tenantRow.Bucket.RecordedCount, ownReport.Value.Conversion.RecordedCount);
        Assert.Equal(tenantRow.Bucket.ConversionRate, ownReport.Value.Conversion.ConversionRate);
    }

    /// <summary>An operator with no attributed conversation, no assignment interval, and no recorded
    /// outcome in the window still gets a response - a real zero <c>OwnOperatorAnalyticsResponse.Bucket</c>,
    /// never a missing row or a failure, the same "own page never 404s" call
    /// <see cref="GetOwnAnalyticsForOperatorHandler"/>'s own remarks make.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheOperatorHasNoDataAtAll_ReturnsAZeroBucket_AndNullLoadAndConversion()
    {
        var (handler, _, _, _) = CreateFixture();

        var result = await handler.HandleAsync(
            new Application.UseCases.GetOwnAnalyticsForOperator.GetOwnAnalyticsForOperator(OperatorA, SiteId, null, null), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value.Bucket.ConversationCount);
        Assert.Null(result.Value.Bucket.AverageFirstResponseSeconds);
        Assert.Null(result.Value.Bucket.AverageDurationSeconds);
        Assert.Equal(0, result.Value.Bucket.MissedCount);
        Assert.Null(result.Value.Load);
        Assert.Null(result.Value.Conversion);
    }

    /// <summary>The two counts stay two counts - `docs/design/decisions.md` §2's naming amendment,
    /// restated for this screen: standard and additional are both present, unmerged, on the same
    /// response, even for an operator who never exceeded capacity (a real, present <c>0</c>, not an
    /// absent field).</summary>
    [Fact]
    public async Task HandleAsync_StandardAndAdditionalIntervals_AreBothPresent_AndNeverCombined()
    {
        var (handler, analyticsStore, loadStore, _) = CreateFixture();
        analyticsStore.Seed(new OperatorAnalyticsResult(
            new OperatorAnalyticsBucket(1, 10.0, 100.0, 0),
            [],
            [new OperatorAnalyticsOperatorBucket(OperatorA, new OperatorAnalyticsBucket(1, 10.0, 100.0, 0), "Ada")],
            [],
            []));
        loadStore.Seed([
            new OperatorLoadSummary(OperatorA, "Ada", 4, 4, 4, 0, [new OperatorLoadBucketEntry("1", 4, 4, 10.0)]),
        ]);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetOwnAnalyticsForOperator.GetOwnAnalyticsForOperator(OperatorA, SiteId, null, null), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value.Load);
        Assert.Equal(4, result.Value.Load!.StandardIntervals);
        Assert.Equal(0, result.Value.Load.AdditionalIntervals);
        Assert.Equal(4, result.Value.Load.IntervalsHeld);
    }
}
