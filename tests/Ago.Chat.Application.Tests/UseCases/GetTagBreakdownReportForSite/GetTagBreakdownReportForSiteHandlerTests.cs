using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetTagBreakdownReportForSite;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetTagBreakdownReportForSite;

public class GetTagBreakdownReportForSiteHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly SiteId OtherSiteId = new(Guid.NewGuid());
    private static readonly OperatorId AdminId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static (GetTagBreakdownReportForSiteHandler Handler, FakeTagBreakdownReadStore Store) CreateFixture(
        bool grantPermission = true)
    {
        var store = new FakeTagBreakdownReadStore();
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(AdminId, SiteId, Permission.SiteConfigure);
        }

        var clock = new FakeClock(Now);
        return (new GetTagBreakdownReportForSiteHandler(store, permissions, clock), store);
    }

    [Fact]
    public async Task HandleAsync_WithoutSiteConfigure_ReturnsForbidden()
    {
        var (handler, _) = CreateFixture(grantPermission: false);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetTagBreakdownReportForSite.GetTagBreakdownReportForSite(AdminId, SiteId, null, null),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenFromIsNotBeforeTo_ReturnsAnalyticsInvalidRange()
    {
        var (handler, _) = CreateFixture();

        var result = await handler.HandleAsync(
            new Application.UseCases.GetTagBreakdownReportForSite.GetTagBreakdownReportForSite(AdminId, SiteId, Now, Now.AddDays(-1)),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Analytics.InvalidRange", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenNoRangeIsSupplied_DefaultsToTheTrailingWindow_AndEchoesItBack()
    {
        var (handler, store) = CreateFixture();

        var result = await handler.HandleAsync(
            new Application.UseCases.GetTagBreakdownReportForSite.GetTagBreakdownReportForSite(AdminId, SiteId, null, null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var expectedFrom = Now.AddDays(-GetTagBreakdownReportForSiteHandler.DefaultWindowDays);
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
            new Application.UseCases.GetTagBreakdownReportForSite.GetTagBreakdownReportForSite(AdminId, SiteId, from, to),
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
            new Application.UseCases.GetTagBreakdownReportForSite.GetTagBreakdownReportForSite(AdminId, SiteId, null, null),
            CancellationToken.None);

        Assert.Equal(SiteId, store.LastSiteId);
        Assert.NotEqual(OtherSiteId, store.LastSiteId);
    }

    [Fact]
    public async Task HandleAsync_MapsTheOverallCoverageFigures_FromTheStore()
    {
        var (handler, store) = CreateFixture();
        store.Seed(new TagBreakdownResult(10, 4, 0.4, []));

        var result = await handler.HandleAsync(
            new Application.UseCases.GetTagBreakdownReportForSite.GetTagBreakdownReportForSite(AdminId, SiteId, null, null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(10, result.Value.TotalConversationCount);
        Assert.Equal(4, result.Value.TaggedConversationCount);
        Assert.Equal(0.4, result.Value.PercentageTagged);
    }

    [Fact]
    public async Task HandleAsync_WhenNoConversationsAreInTheWindow_PercentageTaggedIsNull_NotZero()
    {
        var (handler, store) = CreateFixture();
        store.Seed(new TagBreakdownResult(0, 0, null, []));

        var result = await handler.HandleAsync(
            new Application.UseCases.GetTagBreakdownReportForSite.GetTagBreakdownReportForSite(AdminId, SiteId, null, null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.PercentageTagged);
    }

    [Fact]
    public async Task HandleAsync_MapsThePerTagBuckets_FromTheStore()
    {
        var (handler, store) = CreateFixture();
        var billingTagId = new TagId(Guid.NewGuid());
        var shippingTagId = new TagId(Guid.NewGuid());
        store.Seed(new TagBreakdownResult(
            10, 6, 0.6,
            [
                new TagBreakdownBucket(billingTagId, "Billing", 4, 2, 1, 3, 2.0 / 3.0),
                new TagBreakdownBucket(shippingTagId, "Shipping", 3, 0, 0, 0, null),
            ]));

        var result = await handler.HandleAsync(
            new Application.UseCases.GetTagBreakdownReportForSite.GetTagBreakdownReportForSite(AdminId, SiteId, null, null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.ByTag.Count);
        var billing = result.Value.ByTag.Single(t => t.TagId == billingTagId.Value);
        Assert.Equal("Billing", billing.TagName);
        Assert.Equal(4, billing.ConversationCount);
        Assert.Equal(2, billing.ConvertedCount);
        Assert.Equal(1, billing.NotConvertedCount);
        Assert.Equal(3, billing.RecordedCount);
        Assert.Equal(2.0 / 3.0, billing.ConversionRate);

        var shipping = result.Value.ByTag.Single(t => t.TagId == shippingTagId.Value);
        Assert.Equal(3, shipping.ConversationCount);
        Assert.Null(shipping.ConversionRate);
    }
}
