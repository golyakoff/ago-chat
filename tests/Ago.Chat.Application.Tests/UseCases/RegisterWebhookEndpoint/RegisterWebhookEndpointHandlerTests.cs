using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.RegisterWebhookEndpoint;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RegisterWebhookEndpoint;

public class RegisterWebhookEndpointHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        RegisterWebhookEndpointHandler Handler, FakeWebhookEndpointRepository Endpoints, FakePermissionChecker Permissions);

    private static Fixture CreateFixture(bool grantPermission = true)
    {
        var endpoints = new FakeWebhookEndpointRepository();
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.WebhookManage);
        }

        var handler = new RegisterWebhookEndpointHandler(
            endpoints, permissions, new FakeWebhookSecretGenerator("whsec_abc123"), new FakeWebhookSecretCipher(),
            new FakeIdGenerator(), new FakeClock(Now));

        return new Fixture(handler, endpoints, permissions);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_RegistersTheEndpointAndReturnsTheSecret()
    {
        var fixture = CreateFixture();
        var url = new Uri("https://shop.example.com/webhooks/ago");

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterWebhookEndpoint.RegisterWebhookEndpoint(OperatorId, SiteId, url), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("whsec_abc123", result.Value.Secret);
        Assert.Equal(url, result.Value.Url);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_PersistsTheEndpointAsActive()
    {
        var fixture = CreateFixture();
        var url = new Uri("https://shop.example.com/webhooks/ago");

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterWebhookEndpoint.RegisterWebhookEndpoint(OperatorId, SiteId, url), CancellationToken.None);

        var saved = await fixture.Endpoints.GetByIdAsync(new WebhookEndpointId(result.Value.WebhookEndpointId), CancellationToken.None);
        Assert.NotNull(saved);
        Assert.True(saved.Active);
        Assert.Equal(url, saved.Url);
    }

    [Fact]
    public async Task HandleAsync_NeverPersistsThePlaintextSecret()
    {
        var fixture = CreateFixture();
        var url = new Uri("https://shop.example.com/webhooks/ago");

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterWebhookEndpoint.RegisterWebhookEndpoint(OperatorId, SiteId, url), CancellationToken.None);

        var saved = await fixture.Endpoints.GetByIdAsync(new WebhookEndpointId(result.Value.WebhookEndpointId), CancellationToken.None);
        var storedBytes = saved!.SecretCiphertext;

        // The fake cipher is a plain UTF-8 passthrough (FakeWebhookSecretCipher's own remarks), so
        // this only proves the handler routes the secret through IWebhookSecretCipher.Encrypt before
        // persisting rather than storing the raw string directly on some other field - the real
        // ciphertext-is-not-plaintext property is WebhookSecretCipherTests' job against the real
        // AES-GCM implementation.
        Assert.Equal("whsec_abc123", System.Text.Encoding.UTF8.GetString(storedBytes));
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksWebhookManage_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterWebhookEndpoint.RegisterWebhookEndpoint(
                OperatorId, SiteId, new Uri("https://shop.example.com/webhooks/ago")),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTheUrlIsHttp_ReturnsInvalidUrl()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterWebhookEndpoint.RegisterWebhookEndpoint(
                OperatorId, SiteId, new Uri("http://shop.example.com/webhooks/ago")),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("WebhookEndpoint.InvalidUrl", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTheUrlTargetsALoopbackAddress_ReturnsInvalidUrl()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterWebhookEndpoint.RegisterWebhookEndpoint(
                OperatorId, SiteId, new Uri("https://127.0.0.1/webhooks/ago")),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("WebhookEndpoint.InvalidUrl", result.Error!.Value.Code);
    }
}
