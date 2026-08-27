using Ago.Chat.Domain;
using Ago.Chat.Worker;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>`15-04`: `6-03`'s own support argument is the floor - "a tenant debugging yesterday's
/// failure" must still find the row. Real Postgres, real FK to a real `webhook_endpoints` row
/// (`testing.md`).</summary>
[Collection(PostgresCollection.Name)]
public sealed class WebhookDeliveryPruneJobTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(30);

    [Fact]
    public async Task PruneAsync_RemovesADelivery_OlderThanTheRetentionWindow()
    {
        var id = await SeedDeliveryAsync(createdAt: Now - RetentionWindow - TimeSpan.FromDays(1));

        await CreateJob().PruneAsync(CancellationToken.None);

        Assert.False(await DeliveryExistsAsync(id));
    }

    [Fact]
    public async Task PruneAsync_LeavesADelivery_YoungerThanTheRetentionWindowAlone()
    {
        var id = await SeedDeliveryAsync(createdAt: Now - TimeSpan.FromDays(1));

        Assert.True(await RunAndCheckSurvivesAsync(id));
    }

    /// <summary>"Yesterday's failure" - `6-03`'s own phrase - is the floor this window must clear, and
    /// this is the direct proof: a delivery from the day before still exists after a prune cycle.</summary>
    [Fact]
    public async Task PruneAsync_LeavesYesterdaysFailureVisible()
    {
        var id = await SeedDeliveryAsync(createdAt: Now - TimeSpan.FromDays(1), status: WebhookDeliveryStatus.Failed);

        Assert.True(await RunAndCheckSurvivesAsync(id));
    }

    private async Task<bool> RunAndCheckSurvivesAsync(Guid id)
    {
        try
        {
            await CreateJob().PruneAsync(CancellationToken.None);
            return await DeliveryExistsAsync(id);
        }
        finally
        {
            await DeleteDeliveryAsync(id);
        }
    }

    private WebhookDeliveryPruneJob CreateJob() =>
        new(fixture.DataSource, new FixedClock(Now),
            Options.Create(new WebhookDeliveryPruneJobOptions { RetentionWindow = RetentionWindow }),
            NullLogger<WebhookDeliveryPruneJob>.Instance);

    private async Task<Guid> SeedDeliveryAsync(DateTimeOffset createdAt, WebhookDeliveryStatus status = WebhookDeliveryStatus.Delivered)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var endpoint = WebhookEndpoint.Register(
            new WebhookEndpointId(Guid.NewGuid()), siteId, new Uri("https://shop.example.com/hooks"), [1, 2, 3], createdAt);
        var deliveryId = new WebhookDeliveryId(Guid.NewGuid());
        var delivery = WebhookDelivery.Record(
            deliveryId, endpoint.Id, Guid.NewGuid(), "MessageAccepted", "{}", attempt: 1, status,
            responseStatus: status == WebhookDeliveryStatus.Delivered ? 200 : null,
            responseSnippet: null, createdAt, deliveredAt: status == WebhookDeliveryStatus.Delivered ? createdAt : null);

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.WebhookEndpoints.Add(endpoint);
        db.WebhookDeliveries.Add(delivery);
        await db.SaveChangesAsync(CancellationToken.None);
        return deliveryId.Value;
    }

    private async Task<bool> DeliveryExistsAsync(Guid id)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WebhookDeliveries.AnyAsync(d => d.Id == new WebhookDeliveryId(id), CancellationToken.None);
    }

    private async Task DeleteDeliveryAsync(Guid id)
    {
        await using var db = fixture.CreateDbContext();
        var row = await db.WebhookDeliveries.SingleOrDefaultAsync(d => d.Id == new WebhookDeliveryId(id), CancellationToken.None);
        if (row is not null)
        {
            db.Remove(row);
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
