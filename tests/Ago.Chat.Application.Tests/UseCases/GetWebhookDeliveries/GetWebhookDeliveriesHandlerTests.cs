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

    private static Fixture CreateFixture(
        bool grantPermission = true, bool endpointRevoked = false, SiteId? endpointSiteId = null)
    {
        var endpoints = new FakeWebhookEndpointRepository();
        var deliveries = new FakeWebhookDeliveryReadStore();
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.WebhookManage);
        }

        var endpoint = WebhookEndpoint.Register(
            new WebhookEndpointId(Guid.NewGuid()), endpointSiteId ?? SiteId, new Uri("https://shop.example.com/hooks"), [1], Now);
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

    /// <summary>
    /// `17-01`: the second half of this handler's `endpoint is null || endpoint.SiteId != query.SiteId`
    /// condition, which had no test at all - the branch was correct and nothing would have failed if a
    /// refactor dropped it.
    ///
    /// <para>It is load-bearing because <see cref="Application.Abstractions.IWebhookDeliveryReadStore"/>'s
    /// query filters on `endpoint_id` alone and never mentions `site_id` - this comparison is the only
    /// thing that establishes the endpoint whose history is about to be read belongs to the site the
    /// permission was just checked against. The endpoint id is client-supplied (a route segment), so
    /// without it a `webhook:manage` holder on their own site could page another tenant's delivery
    /// history - which carries that tenant's endpoint URLs, response codes and body snippets.</para>
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenTheEndpointBelongsToAnotherSite_ReturnsNotFound_AndReadsNoHistory()
    {
        var otherSite = new SiteId(Guid.NewGuid());
        var fixture = CreateFixture(endpointSiteId: otherSite);
        fixture.Deliveries.Seed(fixture.Endpoint.Id, new WebhookDeliverySummaryItem(
            new WebhookDeliveryId(Guid.NewGuid()), "conversation.assigned", 1, WebhookDeliveryStatus.Delivered,
            200, "OK", Now, Now));

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetWebhookDeliveries.GetWebhookDeliveries(fixture.Endpoint.Id, OperatorId, SiteId, null, 50),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        // Indistinguishable from an endpoint that does not exist - never "it exists, just not yours".
        Assert.Equal("WebhookEndpoint.NotFound", result.Error!.Value.Code);
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
