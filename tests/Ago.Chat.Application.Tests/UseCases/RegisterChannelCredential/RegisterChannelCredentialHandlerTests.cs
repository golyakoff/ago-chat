using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RegisterChannelCredential;

public class RegisterChannelCredentialHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        Application.UseCases.RegisterChannelCredential.RegisterChannelCredentialHandler Handler,
        FakeChannelCredentialRepository Credentials);

    private static Fixture CreateFixture(bool grantPermission = true, string webhookSecret = "wh_secret_abc")
    {
        var credentials = new FakeChannelCredentialRepository();
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ChannelManage);
        }

        var handler = new Application.UseCases.RegisterChannelCredential.RegisterChannelCredentialHandler(
            credentials, permissions, new FakeChannelCredentialCipher(), new FakeWebhookSecretGenerator(webhookSecret),
            new FakeIdGenerator(), new FakeClock(Now));

        return new Fixture(handler, credentials);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_RegistersAnActiveCredential()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterChannelCredential.RegisterChannelCredential(
                OperatorId, SiteId, ChannelKind.Max, "shop-bot-token"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var saved = await fixture.Credentials.GetByIdAsync(result.Value.ChannelCredentialId, CancellationToken.None);
        Assert.NotNull(saved);
        Assert.True(saved.Active);
        Assert.Equal(SiteId, saved.SiteId);
        Assert.Equal(ChannelKind.Max, saved.Kind);
    }

    [Fact]
    public async Task HandleAsync_ReturnsTheWebhookSecretButNeverTheToken()
    {
        var fixture = CreateFixture(webhookSecret: "wh_secret_xyz");

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterChannelCredential.RegisterChannelCredential(
                OperatorId, SiteId, ChannelKind.Max, "shop-bot-token"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("wh_secret_xyz", result.Value.WebhookSecret);
        // adr/0069: the token itself never appears anywhere in the result - there is no field it could
        // even be assigned to, which is the point being tested here (a compile-time guarantee made
        // observable): RegisteredChannelCredential carries ChannelCredentialId, Kind, WebhookSecret,
        // CreatedAt and nothing shaped like the shop's own secret.
    }

    [Fact]
    public async Task HandleAsync_NeverPersiststheWebhookSecretInReadableForm()
    {
        var fixture = CreateFixture(webhookSecret: "wh_secret_xyz");

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterChannelCredential.RegisterChannelCredential(
                OperatorId, SiteId, ChannelKind.Max, "shop-bot-token"),
            CancellationToken.None);

        var saved = await fixture.Credentials.GetByIdAsync(result.Value.ChannelCredentialId, CancellationToken.None);
        // The stored hash must not equal a plain UTF-8 encoding of the secret - if it did, the
        // "hash, not ciphertext" design (ChannelCredential's own remarks) would be a no-op.
        Assert.NotEqual(System.Text.Encoding.UTF8.GetBytes("wh_secret_xyz"), saved!.WebhookSecretHash);
        Assert.True(saved.MatchesWebhookSecret("wh_secret_xyz"));
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksChannelManage_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterChannelCredential.RegisterChannelCredential(
                OperatorId, SiteId, ChannelKind.Max, "shop-bot-token"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTheTokenIsEmpty_ReturnsInvalidToken()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterChannelCredential.RegisterChannelCredential(
                OperatorId, SiteId, ChannelKind.Max, "   "),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("ChannelCredential.InvalidToken", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenAnActiveCredentialAlreadyExistsForThisChannel_ReturnsAlreadyConnected()
    {
        var fixture = CreateFixture();
        var first = await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterChannelCredential.RegisterChannelCredential(
                OperatorId, SiteId, ChannelKind.Max, "first-token"),
            CancellationToken.None);
        Assert.True(first.IsSuccess);

        var second = await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterChannelCredential.RegisterChannelCredential(
                OperatorId, SiteId, ChannelKind.Max, "second-token"),
            CancellationToken.None);

        Assert.True(second.IsFailure);
        Assert.Equal("ChannelCredential.AlreadyConnected", second.Error!.Value.Code);
    }

    /// <summary>`14-08`: the one field only VK's own connect endpoint ever supplies - this handler
    /// stays channel-neutral by simply forwarding it, the same "not a MAX-only fact" reasoning
    /// <see cref="Application.UseCases.RegisterChannelCredential.RegisterChannelCredential"/>'s own
    /// remarks state.</summary>
    [Fact]
    public async Task HandleAsync_WithAProviderAccountId_PersistsItOnTheCredential()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterChannelCredential.RegisterChannelCredential(
                OperatorId, SiteId, ChannelKind.Vk, "community-token", ProviderAccountId: "555555"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var saved = await fixture.Credentials.GetByIdAsync(result.Value.ChannelCredentialId, CancellationToken.None);
        Assert.Equal("555555", saved!.ProviderAccountId);
    }

    [Fact]
    public async Task HandleAsync_AfterTheExistingCredentialIsRevoked_AllowsRegisteringAReplacement()
    {
        var fixture = CreateFixture();
        var first = await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterChannelCredential.RegisterChannelCredential(
                OperatorId, SiteId, ChannelKind.Max, "first-token"),
            CancellationToken.None);
        var saved = await fixture.Credentials.GetByIdAsync(first.Value.ChannelCredentialId, CancellationToken.None);
        saved!.Revoke();
        await fixture.Credentials.SaveAsync(saved, CancellationToken.None);

        var second = await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterChannelCredential.RegisterChannelCredential(
                OperatorId, SiteId, ChannelKind.Max, "second-token"),
            CancellationToken.None);

        Assert.True(second.IsSuccess);
    }
}
