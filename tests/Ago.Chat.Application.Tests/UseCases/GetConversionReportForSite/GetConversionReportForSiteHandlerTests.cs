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
        Assert.Equal(expectedFrom, store.LastFrom);
        Assert.Equal(Now, store.LastTo);
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
        Assert.Equal(from, store.LastFrom);
        Assert.Equal(to, store.LastTo);
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
