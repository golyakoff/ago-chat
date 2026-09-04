using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetConversionReportForSite;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetConversionReportForSite;

public class GetConversionReportForSiteHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly SiteId OtherSiteId = new(Guid.NewGuid());
    private static readonly OperatorId AdminId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static (GetConversionReportForSiteHandler Handler, FakeConversionReportReadStore Store) CreateFixture(
        bool grantPermission = true)
    {
        var store = new FakeConversionReportReadStore();
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(AdminId, SiteId, Permission.SiteConfigure);
        }

        var clock = new FakeClock(Now);
        return (new GetConversionReportForSiteHandler(store, permissions, clock), store);
    }

    [Fact]
    public async Task HandleAsync_WithoutSiteConfigure_ReturnsForbidden()
    {
        var (handler, _) = CreateFixture(grantPermission: false);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetConversionReportForSite.GetConversionReportForSite(AdminId, SiteId, null, null),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenFromIsNotBeforeTo_ReturnsAnalyticsInvalidRange()
    {
        var (handler, _) = CreateFixture();

        var result = await handler.HandleAsync(
            new Application.UseCases.GetConversionReportForSite.GetConversionReportForSite(AdminId, SiteId, Now, Now.AddDays(-1)),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Analytics.InvalidRange", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenNoRangeIsSupplied_DefaultsToTheTrailingWindow_AndEchoesItBack()
    {
        var (handler, store) = CreateFixture();

        var result = await handler.HandleAsync(
            new Application.UseCases.GetConversionReportForSite.GetConversionReportForSite(AdminId, SiteId, null, null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var expectedFrom = Now.AddDays(-GetConversionReportForSiteHandler.DefaultWindowDays);
        Assert.Equal(expectedFrom, result.Value.From);
        Assert.Equal(Now, result.Value.To);
        Assert.Equal(expectedFrom, store.Calls[0].From);
        Assert.Equal(Now, store.Calls[0].To);
    }

    [Fact]
    public async Task HandleAsync_WhenARangeIsSupplied_PassesItThroughUnchanged()
    {
        var (handler, store) = CreateFixture();
        var from = Now.AddDays(-10);
        var to = Now.AddDays(-1);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetConversionReportForSite.GetConversionReportForSite(AdminId, SiteId, from, to),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(from, result.Value.From);
        Assert.Equal(to, result.Value.To);
        Assert.Equal(from, store.Calls[0].From);
        Assert.Equal(to, store.Calls[0].To);
    }

    [Fact]
    public async Task HandleAsync_PassesTheCallersOwnSiteId_NeverAnother()
    {
        var (handler, store) = CreateFixture();

        await handler.HandleAsync(
            new Application.UseCases.GetConversionReportForSite.GetConversionReportForSite(AdminId, SiteId, null, null),
            CancellationToken.None);

        Assert.Equal(SiteId, store.LastSiteId);
        Assert.NotEqual(OtherSiteId, store.LastSiteId);
    }

    /// <summary>`23-16`: the handler now calls the read store twice per request - the current window,
    /// then the immediately preceding window of equal length (`PrecedingPeriod`'s own remarks). Both
    /// calls must carry the caller's own site, never a swapped or wrong one - the "tenant-isolation test
    /// covers the comparison window's own query" the item's own Done-when asks for, proven here at the
    /// handler's own orchestration boundary since the comparison window reuses the identical
    /// site-scoped port `GetConversionReportAsync_NeverReturnsAnotherSitesConversations` already proves
    /// safe against a real Postgres for the primary window.</summary>
    [Fact]
    public async Task HandleAsync_CallsTheStoreTwice_BothCallsCarryingTheCallersOwnSite_NeverAnother()
    {
        var (handler, store) = CreateFixture();
        var from = Now.AddDays(-10);
        var to = Now.AddDays(-1);

        await handler.HandleAsync(
            new Application.UseCases.GetConversionReportForSite.GetConversionReportForSite(AdminId, SiteId, from, to),
            CancellationToken.None);

        Assert.Equal(2, store.Calls.Count);
        Assert.All(store.Calls, call => Assert.Equal(SiteId, call.SiteId));
        Assert.All(store.Calls, call => Assert.NotEqual(OtherSiteId, call.SiteId));
    }

    /// <summary>The preceding window's own arithmetic: equal length, ending exactly where the current
    /// window begins - no gap, no overlap (`PrecedingPeriod.Before`'s own remarks).</summary>
    [Fact]
    public async Task HandleAsync_TheSecondCall_IsThePrecedingWindowOfEqualLength()
    {
        var (handler, store) = CreateFixture();
        var from = Now.AddDays(-10);
        var to = Now.AddDays(-1);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetConversionReportForSite.GetConversionReportForSite(AdminId, SiteId, from, to),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(from, result.Value.PreviousTo);
        Assert.Equal(from - (to - from), result.Value.PreviousFrom);
        Assert.Equal(result.Value.PreviousFrom, store.Calls[1].From);
        Assert.Equal(result.Value.PreviousTo, store.Calls[1].To);
    }

    [Fact]
    public async Task HandleAsync_MapsThePreviousOverallBucket_FromTheStoresSecondCall()
    {
        var (handler, store) = CreateFixture();
        store.SeedSequence(
            new ConversionReportResult(new ConversionBucket(6, 2, 0, 0, 8, 0.75), []),
            new ConversionReportResult(new ConversionBucket(3, 3, 0, 0, 6, 0.5), []));

        var result = await handler.HandleAsync(
            new Application.UseCases.GetConversionReportForSite.GetConversionReportForSite(AdminId, SiteId, null, null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(6, result.Value.Overall.ConvertedCount);
        Assert.Equal(0.75, result.Value.Overall.ConversionRate);
        Assert.Equal(3, result.Value.PreviousOverall.ConvertedCount);
        Assert.Equal(3, result.Value.PreviousOverall.NotConvertedCount);
        Assert.Equal(0.5, result.Value.PreviousOverall.ConversionRate);
    }

    [Fact]
    public async Task HandleAsync_MapsTheOverallBucketFromTheStore()
    {
        var (handler, store) = CreateFixture();
        store.Seed(new ConversionReportResult(
            new ConversionBucket(ConvertedCount: 6, NotConvertedCount: 2, FollowUpNeededCount: 1, UnsetCount: 11, RecordedCount: 8, ConversionRate: 0.75),
            []));

        var result = await handler.HandleAsync(
            new Application.UseCases.GetConversionReportForSite.GetConversionReportForSite(AdminId, SiteId, null, null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(6, result.Value.Overall.ConvertedCount);
        Assert.Equal(2, result.Value.Overall.NotConvertedCount);
        Assert.Equal(1, result.Value.Overall.FollowUpNeededCount);
        Assert.Equal(11, result.Value.Overall.UnsetCount);
        Assert.Equal(8, result.Value.Overall.RecordedCount);
        Assert.Equal(0.75, result.Value.Overall.ConversionRate);
    }

    [Fact]
    public async Task HandleAsync_MapsThePerOperatorBucketsFromTheStore()
    {
        var (handler, store) = CreateFixture();
        var operatorA = new OperatorId(Guid.NewGuid());
        var operatorB = new OperatorId(Guid.NewGuid());
        store.Seed(new ConversionReportResult(
            new ConversionBucket(3, 1, 0, 2, 4, 0.75),
            [
                new ConversionOperatorBucket(operatorA, new ConversionBucket(2, 0, 0, 1, 2, 1.0)),
                new ConversionOperatorBucket(operatorB, new ConversionBucket(1, 1, 0, 1, 2, 0.5)),
            ]));

        var result = await handler.HandleAsync(
            new Application.UseCases.GetConversionReportForSite.GetConversionReportForSite(AdminId, SiteId, null, null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.ByOperator.Count);
        var a = result.Value.ByOperator.Single(o => o.OperatorId == operatorA.Value);
        Assert.Equal(2, a.Bucket.ConvertedCount);
        Assert.Equal(1.0, a.Bucket.ConversionRate);
        var b = result.Value.ByOperator.Single(o => o.OperatorId == operatorB.Value);
        Assert.Equal(1, b.Bucket.NotConvertedCount);
        Assert.Equal(0.5, b.Bucket.ConversionRate);
    }

    [Fact]
    public async Task HandleAsync_WhenNothingHasBeenRecorded_ConversionRateIsNull_NotZero()
    {
        var (handler, store) = CreateFixture();
        store.Seed(new ConversionReportResult(new ConversionBucket(0, 0, 0, 5, 0, null), []));

        var result = await handler.HandleAsync(
            new Application.UseCases.GetConversionReportForSite.GetConversionReportForSite(AdminId, SiteId, null, null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.Overall.ConversionRate);
        Assert.Equal(5, result.Value.Overall.UnsetCount);
    }
}
