using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Worker;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// This item's own TTL half, real Postgres and real MinIO (<see cref="AttachmentFixture"/> - the same
/// combination <see cref="SiteExportIntegrationTests"/> already uses for the export archive itself,
/// no Keycloak needed here for the identical reason that file states).
/// </summary>
[Collection(AttachmentCollection.Name)]
public sealed class SiteExportPruneJobTests(AttachmentFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(7);

    [Fact]
    public async Task PruneAsync_ExpiresAReadyRequest_OlderThanTheRetentionWindow_AndDeletesItsObject()
    {
        var siteId = await SeedSiteAsync("prune-site-1");
        var objectKey = $"exports/site/{siteId.Value}/{Guid.NewGuid():N}.zip";
        await UploadRealObjectAsync(objectKey);
        var exportId = await SeedReadyExportAsync(siteId, objectKey, completedAt: Now - RetentionWindow - TimeSpan.FromDays(1));

        var expired = await CreateJob().PruneAsync(CancellationToken.None);

        Assert.Equal(1, expired);
        var (status, storedObjectKey) = await ReadStatusAsync(exportId);
        Assert.Equal("Expired", status);
        Assert.Null(storedObjectKey);
        Assert.Null(await fixture.FileStorage.GetMetadataAsync(new ObjectKey(objectKey), CancellationToken.None));
    }

    [Fact]
    public async Task PruneAsync_LeavesAReadyRequest_YoungerThanTheRetentionWindowAlone()
    {
        var siteId = await SeedSiteAsync("prune-site-2");
        var objectKey = $"exports/site/{siteId.Value}/{Guid.NewGuid():N}.zip";
        await UploadRealObjectAsync(objectKey);
        var exportId = await SeedReadyExportAsync(siteId, objectKey, completedAt: Now - TimeSpan.FromDays(1));

        var expired = await CreateJob().PruneAsync(CancellationToken.None);

        Assert.Equal(0, expired);
        var (status, storedObjectKey) = await ReadStatusAsync(exportId);
        Assert.Equal("Ready", status);
        Assert.Equal(objectKey, storedObjectKey);
        Assert.NotNull(await fixture.FileStorage.GetMetadataAsync(new ObjectKey(objectKey), CancellationToken.None));
    }

    /// <summary>A `Pending` request has no `CompletedAt` and no object yet - it must never be swept
    /// regardless of how old `RequestedAt` is. A `Failed` request has a `CompletedAt` but never
    /// produced an object either - the sweep's own `WHERE status = 'Ready'` predicate must exclude
    /// both, not merely "happen to" because neither is old enough in this test.</summary>
    [Fact]
    public async Task PruneAsync_LeavesPendingAndFailedRequestsAlone_RegardlessOfAge()
    {
        var siteId = await SeedSiteAsync("prune-site-3");
        var veryOld = Now - RetentionWindow - TimeSpan.FromDays(365);

        var pendingExportId = await SeedPendingExportAsync(siteId, requestedAt: veryOld);
        var failedExportId = await SeedFailedExportAsync(siteId, completedAt: veryOld);

        var expired = await CreateJob().PruneAsync(CancellationToken.None);

        Assert.Equal(0, expired);
        Assert.Equal("Pending", (await ReadStatusAsync(pendingExportId)).Status);
        Assert.Equal("Failed", (await ReadStatusAsync(failedExportId)).Status);
    }

    private SiteExportPruneJob CreateJob() => new(
        fixture.DataSource,
        fixture.FileStorage,
        new FixedClock(Now),
        Options.Create(new SiteExportPruneJobOptions { RetentionWindow = RetentionWindow }),
        NullLogger<SiteExportPruneJob>.Instance);

    private async Task<SiteId> SeedSiteAsync(string name)
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [], name));
        await db.SaveChangesAsync();
        return siteId;
    }

    private async Task<Guid> SeedReadyExportAsync(SiteId siteId, string objectKey, DateTimeOffset completedAt)
    {
        var exportId = Guid.NewGuid();
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            insert into export_requests (id, site_id, requested_by, status, object_key, requested_at, completed_at)
            values (@id, @siteId, @requestedBy, 'Ready', @objectKey, @requestedAt, @completedAt)
            """,
            new
            {
                id = exportId,
                siteId = siteId.Value,
                requestedBy = Guid.NewGuid(),
                objectKey,
                requestedAt = completedAt - TimeSpan.FromMinutes(1),
                completedAt,
            });
        return exportId;
    }

    private async Task<Guid> SeedPendingExportAsync(SiteId siteId, DateTimeOffset requestedAt)
    {
        var exportId = Guid.NewGuid();
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            insert into export_requests (id, site_id, requested_by, status, requested_at)
            values (@id, @siteId, @requestedBy, 'Pending', @requestedAt)
            """,
            new { id = exportId, siteId = siteId.Value, requestedBy = Guid.NewGuid(), requestedAt });
        return exportId;
    }

    private async Task<Guid> SeedFailedExportAsync(SiteId siteId, DateTimeOffset completedAt)
    {
        var exportId = Guid.NewGuid();
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            insert into export_requests (id, site_id, requested_by, status, failure_reason, requested_at, completed_at)
            values (@id, @siteId, @requestedBy, 'Failed', 'boom', @requestedAt, @completedAt)
            """,
            new
            {
                id = exportId,
                siteId = siteId.Value,
                requestedBy = Guid.NewGuid(),
                requestedAt = completedAt - TimeSpan.FromMinutes(1),
                completedAt,
            });
        return exportId;
    }

    private async Task<(string Status, string? ObjectKey)> ReadStatusAsync(Guid exportId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        var row = await connection.QuerySingleAsync<(string status, string? object_key)>(
            "select status, object_key from export_requests where id = @id", new { id = exportId });
        return (row.status, row.object_key);
    }

    private async Task UploadRealObjectAsync(string key)
    {
        var presigned = await fixture.FileStorage.CreateUploadAsync(
            new ObjectKey(key), new UploadConstraints("application/zip", 5, TimeSpan.FromMinutes(5)), CancellationToken.None);
        using var http = new HttpClient();
        using var content = new ByteArrayContent("12345"u8.ToArray());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
        var response = await http.PutAsync(presigned.Url, content);
        response.EnsureSuccessStatusCode();
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
