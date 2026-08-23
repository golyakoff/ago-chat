using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.RevokeWebhookEndpoint;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RevokeWebhookEndpoint;

public class RevokeWebhookEndpointHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(RevokeWebhookEndpointHandler Handler, FakeWebhookEndpointRepository Endpoints, WebhookEndpoint Endpoint);

    private static Fixture CreateFixture(bool grantPermission = true, SiteId? endpointSiteId = null)
    {
        var endpoints = new FakeWebhookEndpointRepository();
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.WebhookManage);
        }

        var endpoint = WebhookEndpoint.Register(
            new WebhookEndpointId(Guid.NewGuid()), endpointSiteId ?? SiteId, new Uri("https://shop.example.com/hooks"), [1], Now);
        endpoints.Seed(endpoint);

        return new Fixture(new RevokeWebhookEndpointHandler(endpoints, permissions), endpoints, endpoint);
    }

    [Fact]
    public async Task HandleAsync_FlipsActiveToFalse()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RevokeWebhookEndpoint.RevokeWebhookEndpoint(fixture.Endpoint.Id, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var stored = await fixture.Endpoints.GetByIdAsync(fixture.Endpoint.Id, CancellationToken.None);
        Assert.False(stored!.Active);
    }

    [Fact]
    public async Task HandleAsync_CalledTwice_IsIdempotent()
    {
        var fixture = CreateFixture();
        var command = new Application.UseCases.RevokeWebhookEndpoint.RevokeWebhookEndpoint(fixture.Endpoint.Id, OperatorId, SiteId);

        var first = await fixture.Handler.HandleAsync(command, CancellationToken.None);
        var second = await fixture.Handler.HandleAsync(command, CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksWebhookManage_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RevokeWebhookEndpoint.RevokeWebhookEndpoint(fixture.Endpoint.Id, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTheEndpointBelongsToAnotherSite_ReturnsNotFound()
    {
        var otherSite = new SiteId(Guid.NewGuid());
        var fixture = CreateFixture(endpointSiteId: otherSite);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RevokeWebhookEndpoint.RevokeWebhookEndpoint(fixture.Endpoint.Id, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("WebhookEndpoint.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTheEndpointDoesNotExist_ReturnsNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RevokeWebhookEndpoint.RevokeWebhookEndpoint(
                new WebhookEndpointId(Guid.NewGuid()), OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("WebhookEndpoint.NotFound", result.Error!.Value.Code);
    }
}
