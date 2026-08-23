namespace Ago.Chat.Domain.Tests;

public class WebhookEndpointTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());

    private static WebhookEndpoint Register() =>
        WebhookEndpoint.Register(
            new WebhookEndpointId(Guid.NewGuid()), SiteId, new Uri("https://shop.example.com/webhooks/ago"), [1, 2, 3], Now);

    [Fact]
    public void Register_StartsActive()
    {
        var endpoint = Register();

        Assert.True(endpoint.Active);
    }

    [Fact]
    public void Revoke_FlipsActiveToFalse()
    {
        var endpoint = Register();

        endpoint.Revoke();

        Assert.False(endpoint.Active);
    }

    [Fact]
    public void Revoke_WhenAlreadyRevoked_Throws()
    {
        var endpoint = Register();
        endpoint.Revoke();

        Assert.Throws<InvalidWebhookEndpointStateException>(() => endpoint.Revoke());
    }
}
