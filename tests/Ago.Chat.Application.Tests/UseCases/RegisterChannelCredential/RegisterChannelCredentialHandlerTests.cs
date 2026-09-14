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
        FakeChannelCredentialRepository Credentials,
        FakeBillingOptionEntitlementProvider Entitlements,
        FakeModuleQuantityGrantStore Grants);

    /// <summary>`23-85`: <paramref name="grantEntitlement"/> defaults to <see langword="true"/> and
    /// covers every <see cref="ChannelKind"/> this suite exercises (<see cref="GrantEveryChannelKind"/>)
    /// - every existing test here predates the entitlement gate and asserts something else entirely
    /// (token validation, the webhook secret, `ProviderAccountId`...), so defaulting to entitled keeps
    /// them proving what they always proved. The gate itself gets its own dedicated tests below, each
    /// of which opts out explicitly.</summary>
    private static Fixture CreateFixture(
        bool grantPermission = true, bool grantEntitlement = true, string webhookSecret = "wh_secret_abc")
    {
        var credentials = new FakeChannelCredentialRepository();
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ChannelManage);
        }

        var entitlements = new FakeBillingOptionEntitlementProvider();
        var grants = new FakeModuleQuantityGrantStore();
        if (grantEntitlement)
        {
            GrantEveryChannelKind(entitlements, grants);
        }

        var handler = new Application.UseCases.RegisterChannelCredential.RegisterChannelCredentialHandler(
            credentials, permissions, entitlements, grants, new FakeChannelCredentialCipher(),
            new FakeWebhookSecretGenerator(webhookSecret), new FakeIdGenerator(), new FakeClock(Now));

        return new Fixture(handler, credentials, entitlements, grants);
    }

    private static void GrantEveryChannelKind(FakeBillingOptionEntitlementProvider entitlements, FakeModuleQuantityGrantStore grants)
    {
        foreach (var kind in Enum.GetValues<ChannelKind>())
        {
            var optionKey = ChannelEntitlementOptionKeys.For(kind);
            var moduleKey = new ModuleKey(optionKey.Value);
            entitlements.Map(optionKey, moduleKey);
            _ = grants.GrantAsync(SiteId, moduleKey, 1, Now, CancellationToken.None);
        }
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

    /// <summary>`23-85`/`adr/0151`: the item's own Done-when box, first bullet - "connecting a channel
    /// without an entitlement is refused, in the handler." Fails-before: reverting the entitlement
    /// check in <c>RegisterChannelCredentialHandler</c> (or granting the permission alone, as this
    /// fixture's own default used to) makes this test fail with the credential registered instead of
    /// refused - proven by <see cref="HandleAsync_WhenPermitted_RegistersAnActiveCredential"/> still
    /// passing under the pre-`23-85` handler shape.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheAccountHasNoChannelEntitlement_ReturnsChannelNotEntitled()
    {
        var fixture = CreateFixture(grantEntitlement: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterChannelCredential.RegisterChannelCredential(
                OperatorId, SiteId, ChannelKind.Telegram, "shop-bot-token"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("ChannelCredential.NotEntitled", result.Error!.Value.Code);
        Assert.Contains("Telegram", result.Error!.Value.Message);
        Assert.Empty(await fixture.Credentials.GetAllActiveAsync(ChannelKind.Telegram, CancellationToken.None));
    }

    /// <summary>An operator with no permission at all learns nothing about entitlement - `Forbidden`
    /// wins over `NotEntitled` when both would apply, the same "the permission gate runs first" order
    /// <c>RegisterChannelCredentialHandler</c>'s own remarks state.</summary>
    [Fact]
    public async Task HandleAsync_WhenNeitherPermittedNorEntitled_ReturnsForbidden_NotNotEntitled()
    {
        var fixture = CreateFixture(grantPermission: false, grantEntitlement: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterChannelCredential.RegisterChannelCredential(
                OperatorId, SiteId, ChannelKind.Telegram, "shop-bot-token"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    /// <summary>`23-85`'s own decision: one entitlement per channel kind, not one class-wide. An
    /// account entitled for Telegram is not thereby entitled for WhatsApp.</summary>
    [Fact]
    public async Task HandleAsync_EntitledForOneChannelKind_DoesNotGrantAnotherKind()
    {
        var fixture = CreateFixture(grantEntitlement: false);
        var telegramOption = ChannelEntitlementOptionKeys.For(ChannelKind.Telegram);
        fixture.Entitlements.Map(telegramOption, new ModuleKey(telegramOption.Value));
        await fixture.Grants.GrantAsync(SiteId, new ModuleKey(telegramOption.Value), 1, Now, CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterChannelCredential.RegisterChannelCredential(
                OperatorId, SiteId, ChannelKind.WhatsApp, "shop-bot-token"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("ChannelCredential.NotEntitled", result.Error!.Value.Code);
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

    /// <summary>`14-11`: the one field only Avito's own connect endpoint ever supplies - the identical
    /// "stays channel-neutral by simply forwarding it" reasoning
    /// <see cref="HandleAsync_WithAProviderAccountId_PersistsItOnTheCredential"/> already proves for
    /// <c>ProviderAccountId</c>.</summary>
    [Fact]
    public async Task HandleAsync_WithARefreshToken_EncryptsAndPersistsIt()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterChannelCredential.RegisterChannelCredential(
                OperatorId, SiteId, ChannelKind.Avito, "avito-access-token", ProviderAccountId: "94235311",
                RefreshToken: "avito-refresh-token"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var saved = await fixture.Credentials.GetByIdAsync(result.Value.ChannelCredentialId, CancellationToken.None);
        // Went through IChannelCredentialCipher.Encrypt - the identical treatment TokenCiphertext
        // already gets (Domain.ChannelCredential's own remarks on why this is reversible, not a hash).
        // FakeChannelCredentialCipher is a passthrough (UTF-8 bytes, not a real transform), so what this
        // proves is that the handler actually calls Encrypt and stores the result, round-trippable via
        // the same cipher - not that the bytes differ from plaintext, which a real AES-256-GCM cipher
        // (Ago.Chat.Infrastructure.Postgres.ChannelCredentialCipher) already proves for TokenCiphertext
        // via a real key elsewhere.
        Assert.NotNull(saved!.RefreshTokenCiphertext);
        Assert.Equal("avito-refresh-token", System.Text.Encoding.UTF8.GetString(saved.RefreshTokenCiphertext));
    }

    [Fact]
    public async Task HandleAsync_WithNoRefreshToken_LeavesItNull()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterChannelCredential.RegisterChannelCredential(
                OperatorId, SiteId, ChannelKind.Max, "shop-bot-token"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var saved = await fixture.Credentials.GetByIdAsync(result.Value.ChannelCredentialId, CancellationToken.None);
        Assert.Null(saved!.RefreshTokenCiphertext);
    }

    [Fact]
    public async Task HandleAsync_WhenTheRefreshTokenIsEmpty_ReturnsInvalidToken()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterChannelCredential.RegisterChannelCredential(
                OperatorId, SiteId, ChannelKind.Avito, "avito-access-token", RefreshToken: "   "),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("ChannelCredential.InvalidToken", result.Error!.Value.Code);
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
