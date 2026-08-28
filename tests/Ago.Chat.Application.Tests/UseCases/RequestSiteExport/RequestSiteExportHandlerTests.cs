using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.RequestSiteExport;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RequestSiteExport;

public class RequestSiteExportHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly SiteExportRateLimitOptions RateLimitOptions = new() { PerSiteCapacity = 3, PerSiteRefillPerSecond = 1.0 / 3600 };

    [Fact]
    public async Task HandleAsync_WhenPermittedAndNotRateLimited_CreatesAPendingRequest_AndReturnsItsId()
    {
        var exportRequests = new FakeExportRequestRepository();
        exportRequests.SeedSite(SiteId);
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteExport);

        var handler = new RequestSiteExportHandler(
            exportRequests, new FakeRateLimiter(), permissions, RateLimitOptions, new FakeIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.RequestSiteExport.RequestSiteExport(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var exportId = result.Value;
        Assert.True(exportRequests.Requests.ContainsKey(exportId));
        Assert.Equal(ExportStatus.Pending, exportRequests.Requests[exportId].Record.Status);
        Assert.Equal(Now, exportRequests.Requests[exportId].Record.RequestedAt);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksSiteExport_ReturnsForbidden_AndCreatesNoRequest()
    {
        var exportRequests = new FakeExportRequestRepository();
        exportRequests.SeedSite(SiteId);
        var permissions = new FakePermissionChecker(); // nothing granted

        var handler = new RequestSiteExportHandler(
            exportRequests, new FakeRateLimiter(), permissions, RateLimitOptions, new FakeIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.RequestSiteExport.RequestSiteExport(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(exportRequests.Requests);
    }

    [Fact]
    public async Task HandleAsync_WhenTheSiteDoesNotExist_ReturnsSiteNotFound()
    {
        var exportRequests = new FakeExportRequestRepository(); // no site seeded
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteExport);

        var handler = new RequestSiteExportHandler(
            exportRequests, new FakeRateLimiter(), permissions, RateLimitOptions, new FakeIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.RequestSiteExport.RequestSiteExport(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTheRateLimitIsExhausted_ReturnsExportRateLimited_AndCreatesNoRequest()
    {
        var exportRequests = new FakeExportRequestRepository();
        exportRequests.SeedSite(SiteId);
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteExport);

        var handler = new RequestSiteExportHandler(
            exportRequests, new RateLimitedFakeRateLimiter(TimeSpan.FromMinutes(5)), permissions, RateLimitOptions,
            new FakeIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.RequestSiteExport.RequestSiteExport(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Export.RateLimited", result.Error!.Value.Code);
        Assert.Empty(exportRequests.Requests);
    }

    // Permission is checked before the rate limit is spent (RequestSiteExportHandler's own remarks on
    // why the ordering is reversed from RegisterSiteHandler/CreateAttachmentHandler): an operator with
    // no permission at all must never consume a share of the site's shared export budget finding that
    // out. Proven by a rate limiter that would deny *any* call, alongside an unpermitted operator - the
    // Forbidden code, not RateLimited, must be what comes back.
    [Fact]
    public async Task HandleAsync_WhenUnpermittedAndRateLimited_StillReturnsForbidden_NotRateLimited()
    {
        var exportRequests = new FakeExportRequestRepository();
        exportRequests.SeedSite(SiteId);
        var permissions = new FakePermissionChecker(); // nothing granted

        var handler = new RequestSiteExportHandler(
            exportRequests, new RateLimitedFakeRateLimiter(TimeSpan.FromMinutes(5)), permissions, RateLimitOptions,
            new FakeIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.RequestSiteExport.RequestSiteExport(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }
}
