using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetWebhookDeliveries;

public class GetWebhookDeliveriesHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        Application.UseCases.GetWebhookDeliveries.GetWebhookDeliveriesHandler Handler,
        FakeWebhookEndpointRepository Endpoints,
        FakeWebhookDeliveryReadStore Deliveries,
        WebhookEndpoint Endpoint);

    private static Fixture CreateFixture(bool grantPermission = true, bool endpointRevoked = false)
    {
        var endpoints = new FakeWebhookEndpointRepository();
        var deliveries = new FakeWebhookDeliveryReadStore();
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.WebhookManage);
        }

        var endpoint = WebhookEndpoint.Register(
            new WebhookEndpointId(Guid.NewGuid()), SiteId, new Uri("https://shop.example.com/hooks"), [1], Now);
        if (endpointRevoked)
        {
            endpoint.Revoke();
        }

        endpoints.Seed(endpoint);

        var handler = new Application.UseCases.GetWebhookDeliveries.GetWebhookDeliveriesHandler(endpoints, deliveries, permissions);
        return new Fixture(handler, endpoints, deliveries, endpoint);
    }

    [Fact]
    public async Task HandleAsync_ReturnsTheEndpointsDeliveryHistory()
    {
        var fixture = CreateFixture();
        var deliveryId = new WebhookDeliveryId(Guid.NewGuid());
        fixture.Deliveries.Seed(fixture.Endpoint.Id, new WebhookDeliverySummaryItem(
            deliveryId, "message.created", 1, WebhookDeliveryStatus.Delivered, 200, "OK", Now, Now));

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetWebhookDeliveries.GetWebhookDeliveries(fixture.Endpoint.Id, OperatorId, SiteId, null, 50),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var dto = Assert.Single(result.Value.Deliveries);
        Assert.Equal(deliveryId.Value, dto.Id);
        Assert.Equal("Delivered", dto.Status);
    }

    [Fact]
    public async Task HandleAsync_WhenTheEndpointHasBeenRevoked_StillReturnsItsDeliveryHistory()
    {
        var fixture = CreateFixture(endpointRevoked: true);
        fixture.Deliveries.Seed(fixture.Endpoint.Id, new WebhookDeliverySummaryItem(
            new WebhookDeliveryId(Guid.NewGuid()), "message.created", 1, WebhookDeliveryStatus.Delivered, 200, "OK", Now, Now));

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetWebhookDeliveries.GetWebhookDeliveries(fixture.Endpoint.Id, OperatorId, SiteId, null, 50),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Deliveries);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksWebhookManage_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetWebhookDeliveries.GetWebhookDeliveries(fixture.Endpoint.Id, OperatorId, SiteId, null, 50),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTheEndpointDoesNotExist_ReturnsNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetWebhookDeliveries.GetWebhookDeliveries(
                new WebhookEndpointId(Guid.NewGuid()), OperatorId, SiteId, null, 50),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("WebhookEndpoint.NotFound", result.Error!.Value.Code);
    }
}
