using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.ListWebhookEndpoints;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ListWebhookEndpoints;

public class ListWebhookEndpointsHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static (ListWebhookEndpointsHandler Handler, FakeWebhookEndpointRepository Endpoints) CreateFixture(
        bool grantPermission = true)
    {
        var endpoints = new FakeWebhookEndpointRepository();
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.WebhookManage);
        }

        return (new ListWebhookEndpointsHandler(endpoints, permissions), endpoints);
    }

    [Fact]
    public async Task HandleAsync_ReturnsEveryEndpointForTheSite_WithoutASecretField()
    {
        var (handler, endpoints) = CreateFixture();
        var endpoint = WebhookEndpoint.Register(
            new WebhookEndpointId(Guid.NewGuid()), SiteId, new Uri("https://shop.example.com/hooks"), [9, 9, 9], Now);
        endpoints.Seed(endpoint);

        var result = await handler.HandleAsync(
            new Application.UseCases.ListWebhookEndpoints.ListWebhookEndpoints(OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var dto = Assert.Single(result.Value.Endpoints);
        Assert.Equal(endpoint.Id.Value, dto.Id);
        Assert.Equal("https://shop.example.com/hooks", dto.Url);
        Assert.True(dto.Active);
        // WebhookEndpointDto (Ago.Chat.Contracts) has no secret-shaped property at all - there is no
        // field to assert null on, which is the point: the wire type cannot carry one even by mistake.
    }

    [Fact]
    public async Task HandleAsync_NeverReturnsAnEndpointFromAnotherSite()
    {
        var (handler, endpoints) = CreateFixture();
        var otherSite = new SiteId(Guid.NewGuid());
        endpoints.Seed(WebhookEndpoint.Register(
            new WebhookEndpointId(Guid.NewGuid()), otherSite, new Uri("https://other.example.com/hooks"), [1], Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.ListWebhookEndpoints.ListWebhookEndpoints(OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Endpoints);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksWebhookManage_ReturnsForbidden()
    {
        var (handler, _) = CreateFixture(grantPermission: false);

        var result = await handler.HandleAsync(
            new Application.UseCases.ListWebhookEndpoints.ListWebhookEndpoints(OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }
}
