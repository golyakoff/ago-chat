using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetSiteExportStatus;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;

namespace Ago.Chat.Application.Tests.UseCases.GetSiteExportStatus;

public class GetSiteExportStatusHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly SiteId OtherSiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteExportOptions Options = new() { DownloadUrlLifetime = TimeSpan.FromMinutes(15) };

    [Fact]
    public async Task HandleAsync_WhenPendingAndPermitted_ReturnsStatus_WithNoDownloadUrl()
    {
        var exportRequests = new FakeExportRequestRepository();
        exportRequests.SeedSite(SiteId);
        await exportRequests.CreateAsync(Guid.NewGuid(), SiteId, OperatorId, Now, CancellationToken.None);
        var exportId = exportRequests.Requests.Keys.Single();

        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteExport);
        var fileStorage = new FakeFileStorage();

        var handler = new GetSiteExportStatusHandler(exportRequests, fileStorage, permissions, Options);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetSiteExportStatus.GetSiteExportStatus(exportId, SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ExportStatus.Pending, result.Value.Status);
        Assert.Null(result.Value.DownloadUrl);
        Assert.Equal(0, fileStorage.CreateDownloadUrlCalls);
    }

    [Fact]
    public async Task HandleAsync_WhenReady_ReturnsAFreshlyMintedDownloadUrl()
    {
        var exportRequests = new FakeExportRequestRepository();
        exportRequests.SeedSite(SiteId);
        var exportId = Guid.NewGuid();
        await exportRequests.CreateAsync(exportId, SiteId, OperatorId, Now, CancellationToken.None);
        exportRequests.SetReady(exportId, "exports/site/x/y.zip", Now.AddMinutes(1));

        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteExport);
        var fileStorage = new FakeFileStorage();

        var handler = new GetSiteExportStatusHandler(exportRequests, fileStorage, permissions, Options);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetSiteExportStatus.GetSiteExportStatus(exportId, SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ExportStatus.Ready, result.Value.Status);
        Assert.NotNull(result.Value.DownloadUrl);
        Assert.Equal(1, fileStorage.CreateDownloadUrlCalls);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksSiteExport_ReturnsForbidden()
    {
        var exportRequests = new FakeExportRequestRepository();
        exportRequests.SeedSite(SiteId);
        var exportId = Guid.NewGuid();
        await exportRequests.CreateAsync(exportId, SiteId, OperatorId, Now, CancellationToken.None);

        var permissions = new FakePermissionChecker(); // nothing granted
        var handler = new GetSiteExportStatusHandler(exportRequests, new FakeFileStorage(), permissions, Options);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetSiteExportStatus.GetSiteExportStatus(exportId, SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    // The cross-tenant guard this item's own Done-when demands proven: an operator who holds
    // SiteExport on a *different* site, polling with that site's id in the route, must not be told
    // this export exists - the same "wrong site is indistinguishable from no such id" shape
    // IErasureRequestRepository's own remarks describe for erasure.
    [Fact]
    public async Task HandleAsync_WhenTheExportBelongsToADifferentSite_ReturnsNotFound_NotForbidden()
    {
        var exportRequests = new FakeExportRequestRepository();
        exportRequests.SeedSite(SiteId);
        exportRequests.SeedSite(OtherSiteId);
        var exportId = Guid.NewGuid();
        // The export genuinely belongs to SiteId...
        await exportRequests.CreateAsync(exportId, SiteId, OperatorId, Now, CancellationToken.None);

        // ...but this operator only holds SiteExport on OtherSiteId, and polls using OtherSiteId's id -
        // exactly what a route-scoped permission check plus a route-scoped repository read defend
        // against together.
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, OtherSiteId, Permission.SiteExport);
        var handler = new GetSiteExportStatusHandler(exportRequests, new FakeFileStorage(), permissions, Options);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetSiteExportStatus.GetSiteExportStatus(exportId, OtherSiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Export.NotFound", result.Error!.Value.Code);
    }
}
