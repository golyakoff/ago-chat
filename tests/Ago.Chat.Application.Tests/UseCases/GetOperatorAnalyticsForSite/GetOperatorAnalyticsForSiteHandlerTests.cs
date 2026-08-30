using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetOperatorAnalyticsForSite;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetOperatorAnalyticsForSite;

public class GetOperatorAnalyticsForSiteHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly SiteId OtherSiteId = new(Guid.NewGuid());
    private static readonly OperatorId AdminId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static (GetOperatorAnalyticsForSiteHandler Handler, FakeOperatorAnalyticsReadStore Store) CreateFixture(
        bool grantPermission = true)
    {
        var store = new FakeOperatorAnalyticsReadStore();
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(AdminId, SiteId, Permission.SiteConfigure);
        }

        var clock = new FakeClock(Now);
        return (new GetOperatorAnalyticsForSiteHandler(store, permissions, clock), store);
    }

    [Fact]
    public async Task HandleAsync_WithoutSiteConfigure_ReturnsForbidden()
    {
        var (handler, _) = CreateFixture(grantPermission: false);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetOperatorAnalyticsForSite.GetOperatorAnalyticsForSite(AdminId, SiteId, null, null),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenFromIsNotBeforeTo_ReturnsAnalyticsInvalidRange()
    {
        var (handler, _) = CreateFixture();

        var result = await handler.HandleAsync(
            new Application.UseCases.GetOperatorAnalyticsForSite.GetOperatorAnalyticsForSite(
                AdminId, SiteId, Now, Now.AddDays(-1)),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Analytics.InvalidRange", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenFromEqualsTo_ReturnsAnalyticsInvalidRange()
    {
        var (handler, _) = CreateFixture();

        var result = await handler.HandleAsync(
            new Application.UseCases.GetOperatorAnalyticsForSite.GetOperatorAnalyticsForSite(AdminId, SiteId, Now, Now),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Analytics.InvalidRange", result.Error!.Value.Code);
    }

    /// <summary>`18-08`'s own bound decision, the same shape `18-01`'s `SearchConversationsHandler`
    /// already establishes: naming no range does not reject the report, it defaults one - and the
    /// response always echoes back exactly what was reported on, so the console can show it rather than
    /// the operator having to infer a silent truncation.</summary>
    [Fact]
    public async Task HandleAsync_WhenNoRangeIsSupplied_DefaultsToTheTrailingWindow_AndEchoesItBack()
    {
        var (handler, store) = CreateFixture();

        var result = await handler.HandleAsync(
            new Application.UseCases.GetOperatorAnalyticsForSite.GetOperatorAnalyticsForSite(AdminId, SiteId, null, null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var expectedFrom = Now.AddDays(-GetOperatorAnalyticsForSiteHandler.DefaultWindowDays);
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
            new Application.UseCases.GetOperatorAnalyticsForSite.GetOperatorAnalyticsForSite(AdminId, SiteId, from, to),
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
            new Application.UseCases.GetOperatorAnalyticsForSite.GetOperatorAnalyticsForSite(AdminId, SiteId, null, null),
            CancellationToken.None);

        Assert.Equal(SiteId, store.LastSiteId);
        Assert.NotEqual(OtherSiteId, store.LastSiteId);
    }

    [Fact]
    public async Task HandleAsync_MapsTheOverallAndPerChannelBucketsFromTheStore()
    {
        var (handler, store) = CreateFixture();
        store.Seed(new OperatorAnalyticsResult(
            new OperatorAnalyticsBucket(ConversationCount: 5, AverageFirstResponseSeconds: 42.5, AverageDurationSeconds: 300.0, MissedCount: 1),
            [
                new OperatorAnalyticsChannelBucket("Widget", new OperatorAnalyticsBucket(3, 30.0, 200.0, 0)),
                new OperatorAnalyticsChannelBucket("Sms", new OperatorAnalyticsBucket(2, 60.0, 400.0, 1)),
            ],
            []));

        var result = await handler.HandleAsync(
            new Application.UseCases.GetOperatorAnalyticsForSite.GetOperatorAnalyticsForSite(AdminId, SiteId, null, null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(5, result.Value.Overall.ConversationCount);
        Assert.Equal(42.5, result.Value.Overall.AverageFirstResponseSeconds);
        Assert.Equal(300.0, result.Value.Overall.AverageDurationSeconds);
        Assert.Equal(1, result.Value.Overall.MissedCount);
        Assert.Equal(2, result.Value.ByChannel.Count);
        var widget = result.Value.ByChannel.Single(c => c.Channel == "Widget");
        Assert.Equal(3, widget.Bucket.ConversationCount);
        Assert.Equal(30.0, widget.Bucket.AverageFirstResponseSeconds);
        Assert.Equal(200.0, widget.Bucket.AverageDurationSeconds);
        Assert.Equal(0, widget.Bucket.MissedCount);
    }

    /// <summary>`18-09`: the handler's own half of the per-operator addition - a pure pass-through of
    /// whatever <see cref="IOperatorAnalyticsReadStore"/> already attributed, proven the same way the
    /// per-channel mapping above already is. The attribution decision itself (first responder, missed
    /// falls back to whoever was assigned) is `OperatorAnalyticsReadStoreTests`' job, against a real
    /// Postgres - this test only proves the handler does not drop or reshuffle what the store returns.
    /// </summary>
    [Fact]
    public async Task HandleAsync_MapsThePerOperatorBucketsFromTheStore()
    {
        var (handler, store) = CreateFixture();
        var operatorA = new OperatorId(Guid.NewGuid());
        var operatorB = new OperatorId(Guid.NewGuid());
        store.Seed(new OperatorAnalyticsResult(
            new OperatorAnalyticsBucket(2, 45.0, 250.0, 0),
            [],
            [
                new OperatorAnalyticsOperatorBucket(operatorA, new OperatorAnalyticsBucket(1, 60.0, 180.0, 0)),
                new OperatorAnalyticsOperatorBucket(operatorB, new OperatorAnalyticsBucket(1, 30.0, null, 1)),
            ]));

        var result = await handler.HandleAsync(
            new Application.UseCases.GetOperatorAnalyticsForSite.GetOperatorAnalyticsForSite(AdminId, SiteId, null, null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.ByOperator.Count);
        var a = result.Value.ByOperator.Single(o => o.OperatorId == operatorA.Value);
        Assert.Equal(1, a.Bucket.ConversationCount);
        Assert.Equal(60.0, a.Bucket.AverageFirstResponseSeconds);
        Assert.Equal(180.0, a.Bucket.AverageDurationSeconds);
        Assert.Equal(0, a.Bucket.MissedCount);
        var b = result.Value.ByOperator.Single(o => o.OperatorId == operatorB.Value);
        Assert.Equal(1, b.Bucket.ConversationCount);
        Assert.Null(b.Bucket.AverageDurationSeconds);
        Assert.Equal(1, b.Bucket.MissedCount);
    }
}
