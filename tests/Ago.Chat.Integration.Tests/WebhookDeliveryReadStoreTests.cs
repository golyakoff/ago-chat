using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Platform.Kernel;

namespace Ago.Chat.Integration.Tests;

[Collection(PostgresCollection.Name)]
public class WebhookDeliveryReadStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly UuidV7Generator IdGenerator = new();

    private async Task<WebhookEndpointId> SeedSiteAndEndpoint()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var endpoint = WebhookEndpoint.Register(
            new WebhookEndpointId(Guid.NewGuid()), siteId, new Uri("https://shop.example.com/hooks"), [1], Now);

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.WebhookEndpoints.Add(endpoint);
        await db.SaveChangesAsync();

        return endpoint.Id;
    }

    private async Task SeedDelivery(WebhookEndpointId endpointId, DateTimeOffset createdAt, WebhookDeliveryStatus status)
    {
        var delivery = WebhookDelivery.Record(
            new WebhookDeliveryId(IdGenerator.NewId(createdAt)), endpointId, Guid.NewGuid(), "message.created", "{\"foo\":1}",
            attempt: 1, status, responseStatus: status == WebhookDeliveryStatus.Delivered ? 200 : null,
            responseSnippet: status == WebhookDeliveryStatus.Delivered ? "OK" : null, createdAt,
            deliveredAt: status == WebhookDeliveryStatus.Delivered ? createdAt : null);

        await using var db = fixture.CreateDbContext();
        var repository = new WebhookDeliveryRepository(db);
        await repository.SaveAsync(delivery, CancellationToken.None);
    }

    [Fact]
    public async Task GetForEndpointAsync_ReturnsDeliveriesNewestFirst()
    {
        var endpointId = await SeedSiteAndEndpoint();
        await SeedDelivery(endpointId, Now, WebhookDeliveryStatus.Delivered);
        await SeedDelivery(endpointId, Now.AddSeconds(1), WebhookDeliveryStatus.Failed);
        await SeedDelivery(endpointId, Now.AddSeconds(2), WebhookDeliveryStatus.Delivered);

        var readStore = new WebhookDeliveryReadStore(fixture.DataSource);
        var page = await readStore.GetForEndpointAsync(endpointId, beforeId: null, pageSize: 50, CancellationToken.None);

        Assert.Equal(3, page.Deliveries.Count);
        Assert.Equal(WebhookDeliveryStatus.Delivered, page.Deliveries[0].Status); // Now+2, newest
        Assert.Equal(WebhookDeliveryStatus.Failed, page.Deliveries[1].Status); // Now+1
        Assert.Equal(WebhookDeliveryStatus.Delivered, page.Deliveries[2].Status); // Now, oldest
        Assert.Null(page.NextBeforeId);
    }

    [Fact]
    public async Task GetForEndpointAsync_PagesWithAKeysetCursor_NoOffset()
    {
        var endpointId = await SeedSiteAndEndpoint();
        for (var i = 0; i < 5; i++)
        {
            await SeedDelivery(endpointId, Now.AddSeconds(i), WebhookDeliveryStatus.Delivered);
        }

        var readStore = new WebhookDeliveryReadStore(fixture.DataSource);

        var firstPage = await readStore.GetForEndpointAsync(endpointId, beforeId: null, pageSize: 2, CancellationToken.None);
        Assert.Equal(2, firstPage.Deliveries.Count);
        Assert.NotNull(firstPage.NextBeforeId);

        var secondPage = await readStore.GetForEndpointAsync(endpointId, firstPage.NextBeforeId, pageSize: 2, CancellationToken.None);
        Assert.Equal(2, secondPage.Deliveries.Count);
        Assert.NotNull(secondPage.NextBeforeId);

        var thirdPage = await readStore.GetForEndpointAsync(endpointId, secondPage.NextBeforeId, pageSize: 2, CancellationToken.None);
        Assert.Single(thirdPage.Deliveries);
        Assert.Null(thirdPage.NextBeforeId);

        // No item repeats or is skipped across pages.
        var allIds = firstPage.Deliveries.Concat(secondPage.Deliveries).Concat(thirdPage.Deliveries).Select(d => d.Id).ToList();
        Assert.Equal(5, allIds.Distinct().Count());
    }

    [Fact]
    public async Task GetForEndpointAsync_NeverReturnsDeliveriesForAnotherEndpoint()
    {
        var endpointId = await SeedSiteAndEndpoint();
        var otherEndpointId = await SeedSiteAndEndpoint();
        await SeedDelivery(endpointId, Now, WebhookDeliveryStatus.Delivered);
        await SeedDelivery(otherEndpointId, Now, WebhookDeliveryStatus.Delivered);

        var readStore = new WebhookDeliveryReadStore(fixture.DataSource);
        var page = await readStore.GetForEndpointAsync(endpointId, beforeId: null, pageSize: 50, CancellationToken.None);

        Assert.Single(page.Deliveries);
    }
}
