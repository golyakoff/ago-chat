using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetSiteExportHistory;
using Ago.Chat.Application.UseCases.GetSiteExportStatus;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetSiteExportHistory;

public class GetSiteExportHistoryHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteExportOptions DownloadUrlOptions = new() { DownloadUrlLifetime = TimeSpan.FromMinutes(15) };
    private static readonly SiteExportPruneJobOptions PruneOptions = new() { RetentionWindow = TimeSpan.FromDays(7) };

    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksSiteExport_ReturnsForbidden()
    {
        var exportRequests = new FakeExportRequestRepository();
        exportRequests.SeedSite(SiteId);
        var permissions = new FakePermissionChecker(); // nothing granted

        var handler = new GetSiteExportHistoryHandler(
            exportRequests, new FakeFileStorage(), permissions, DownloadUrlOptions, PruneOptions);

        var result = await handler.HandleAsync(new Application.UseCases.GetSiteExportHistory.GetSiteExportHistory(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_ReturnsEveryRequest_NewestFirst()
    {
        var exportRequests = new FakeExportRequestRepository();
        exportRequests.SeedSite(SiteId);
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteExport);

        var firstExportId = Guid.NewGuid();
        await exportRequests.CreateAsync(firstExportId, SiteId, OperatorId, Now, CancellationToken.None);
        var secondExportId = Guid.NewGuid();
        await exportRequests.CreateAsync(secondExportId, SiteId, OperatorId, Now.AddMinutes(5), CancellationToken.None);

        var handler = new GetSiteExportHistoryHandler(
            exportRequests, new FakeFileStorage(), permissions, DownloadUrlOptions, PruneOptions);

        var result = await handler.HandleAsync(new Application.UseCases.GetSiteExportHistory.GetSiteExportHistory(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Count);
        Assert.Equal(secondExportId, result.Value[0].ExportId); // newest (later RequestedAt) first
        Assert.Equal(firstExportId, result.Value[1].ExportId);
    }

    [Fact]
    public async Task HandleAsync_WhenPending_HasNoDownloadUrl_AndNoExpiresAt()
    {
        var exportRequests = new FakeExportRequestRepository();
        exportRequests.SeedSite(SiteId);
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteExport);

        var exportId = Guid.NewGuid();
        await exportRequests.CreateAsync(exportId, SiteId, OperatorId, Now, CancellationToken.None);

        var fileStorage = new FakeFileStorage();
        var handler = new GetSiteExportHistoryHandler(exportRequests, fileStorage, permissions, DownloadUrlOptions, PruneOptions);

        var result = await handler.HandleAsync(new Application.UseCases.GetSiteExportHistory.GetSiteExportHistory(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value);
        Assert.Equal(ExportStatus.Pending, item.Status);
        Assert.Null(item.DownloadUrl);
        Assert.Null(item.ExpiresAt);
        Assert.Equal(0, fileStorage.CreateDownloadUrlCalls);
    }

    [Fact]
    public async Task HandleAsync_WhenReady_HasAFreshDownloadUrl_AndExpiresAtCompletedAtPlusRetentionWindow()
    {
        var exportRequests = new FakeExportRequestRepository();
        exportRequests.SeedSite(SiteId);
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteExport);

        var exportId = Guid.NewGuid();
        await exportRequests.CreateAsync(exportId, SiteId, OperatorId, Now, CancellationToken.None);
        var completedAt = Now.AddMinutes(1);
        exportRequests.SetReady(exportId, "exports/site/x/y.zip", completedAt);

        var fileStorage = new FakeFileStorage();
        var handler = new GetSiteExportHistoryHandler(exportRequests, fileStorage, permissions, DownloadUrlOptions, PruneOptions);

        var result = await handler.HandleAsync(new Application.UseCases.GetSiteExportHistory.GetSiteExportHistory(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value);
        Assert.Equal(ExportStatus.Ready, item.Status);
        Assert.NotNull(item.DownloadUrl);
        Assert.Equal(1, fileStorage.CreateDownloadUrlCalls);
        Assert.Equal(completedAt + PruneOptions.RetentionWindow, item.ExpiresAt);
    }

    [Fact]
    public async Task HandleAsync_WhenFailed_HasNoDownloadUrl_AndNoExpiresAt()
    {
        var exportRequests = new FakeExportRequestRepository();
        exportRequests.SeedSite(SiteId);
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteExport);

        var exportId = Guid.NewGuid();
        await exportRequests.CreateAsync(exportId, SiteId, OperatorId, Now, CancellationToken.None);
        exportRequests.SetFailed(exportId, "boom", Now.AddMinutes(1));

        var fileStorage = new FakeFileStorage();
        var handler = new GetSiteExportHistoryHandler(exportRequests, fileStorage, permissions, DownloadUrlOptions, PruneOptions);

        var result = await handler.HandleAsync(new Application.UseCases.GetSiteExportHistory.GetSiteExportHistory(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value);
        Assert.Equal(ExportStatus.Failed, item.Status);
        Assert.Equal("boom", item.FailureReason);
        Assert.Null(item.DownloadUrl);
        Assert.Null(item.ExpiresAt);
        Assert.Equal(0, fileStorage.CreateDownloadUrlCalls);
    }

    /// <summary>An `Expired` row (SiteExportPruneJob already deleted its object) must never mint a
    /// download URL and must never compute a second expiry date for an object that is already gone -
    /// SiteExportHistoryItem's own remarks on why only `Ready` carries either.</summary>
    [Fact]
    public async Task HandleAsync_WhenExpired_HasNoDownloadUrl_AndNoExpiresAt()
    {
        var exportRequests = new FakeExportRequestRepository();
        exportRequests.SeedSite(SiteId);
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteExport);

        var exportId = Guid.NewGuid();
        await exportRequests.CreateAsync(exportId, SiteId, OperatorId, Now, CancellationToken.None);
        exportRequests.SetReady(exportId, "exports/site/x/y.zip", Now.AddMinutes(1));
        exportRequests.SetExpired(exportId);

        var fileStorage = new FakeFileStorage();
        var handler = new GetSiteExportHistoryHandler(exportRequests, fileStorage, permissions, DownloadUrlOptions, PruneOptions);

        var result = await handler.HandleAsync(new Application.UseCases.GetSiteExportHistory.GetSiteExportHistory(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value);
        Assert.Equal(ExportStatus.Expired, item.Status);
        Assert.Null(item.DownloadUrl);
        Assert.Null(item.ExpiresAt);
        Assert.Equal(0, fileStorage.CreateDownloadUrlCalls);
    }

    [Fact]
    public async Task HandleAsync_NeverReturnsAnotherSitesExportRequests()
    {
        var otherSiteId = new SiteId(Guid.NewGuid());
        var exportRequests = new FakeExportRequestRepository();
        exportRequests.SeedSite(SiteId);
        exportRequests.SeedSite(otherSiteId);
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteExport);

        await exportRequests.CreateAsync(Guid.NewGuid(), SiteId, OperatorId, Now, CancellationToken.None);
        await exportRequests.CreateAsync(Guid.NewGuid(), otherSiteId, OperatorId, Now, CancellationToken.None);

        var handler = new GetSiteExportHistoryHandler(
            exportRequests, new FakeFileStorage(), permissions, DownloadUrlOptions, PruneOptions);

        var result = await handler.HandleAsync(new Application.UseCases.GetSiteExportHistory.GetSiteExportHistory(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value);
    }
}
