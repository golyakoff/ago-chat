using System.Reflection;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetChannelCredentialStatus;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetChannelCredentialStatus;

public class GetChannelCredentialStatusHandlerTests
{
    private static readonly SiteId SiteA = new(Guid.NewGuid());
    private static readonly SiteId SiteB = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset RegisteredAt = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        GetChannelCredentialStatusHandler Handler, FakeChannelCredentialRepository Credentials,
        FakePermissionChecker Permissions, FakeBillingOptionEntitlementProvider Entitlements,
        FakeModuleQuantityGrantStore Grants);

    /// <summary>`23-85`: <paramref name="grantEntitlement"/> defaults to <see langword="true"/>, for
    /// <paramref name="entitledSite"/> (defaulting to <see cref="SiteA"/>) and every
    /// <see cref="ChannelKind"/> - every pre-existing test here predates the entitlement gate. The
    /// gate's own tests opt out, or grant a different site, explicitly below.</summary>
    private static Fixture CreateFixture(bool grantEntitlement = true, SiteId? entitledSite = null)
    {
        var credentials = new FakeChannelCredentialRepository();
        var permissions = new FakePermissionChecker();
        var entitlements = new FakeBillingOptionEntitlementProvider();
        var grants = new FakeModuleQuantityGrantStore();
        if (grantEntitlement)
        {
            var site = entitledSite ?? SiteA;
            foreach (var kind in Enum.GetValues<ChannelKind>())
            {
                var optionKey = ChannelEntitlementOptionKeys.For(kind);
                var moduleKey = new ModuleKey(optionKey.Value);
                entitlements.Map(optionKey, moduleKey);
                _ = grants.GrantAsync(site, moduleKey, 1, RegisteredAt, CancellationToken.None);
            }
        }

        var handler = new GetChannelCredentialStatusHandler(credentials, permissions, entitlements, grants);
        return new Fixture(handler, credentials, permissions, entitlements, grants);
    }

    private static ChannelCredential ActiveCredential(SiteId siteId, ChannelKind kind, DateTimeOffset now) =>
        Domain.ChannelCredential.Register(
            new ChannelCredentialId(Guid.NewGuid()), siteId, kind, tokenCiphertext: [1, 2, 3],
            webhookSecretHash: [4, 5, 6], now);

    [Fact]
    public async Task HandleAsync_WhenNoCredentialExists_ReturnsNotConnected()
    {
        var fixture = CreateFixture();
        fixture.Permissions.Grant(OperatorId, SiteA, Permission.ChannelManage);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetChannelCredentialStatus.GetChannelCredentialStatus(OperatorId, SiteA, ChannelKind.Telegram),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.ChannelCredentialId);
        Assert.Null(result.Value.CreatedAt);
    }

    [Fact]
    public async Task HandleAsync_WhenAnActiveCredentialExists_ReturnsItsIdAndCreatedAt()
    {
        var fixture = CreateFixture();
        fixture.Permissions.Grant(OperatorId, SiteA, Permission.ChannelManage);
        var credential = ActiveCredential(SiteA, ChannelKind.Telegram, RegisteredAt);
        fixture.Credentials.Seed(credential);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetChannelCredentialStatus.GetChannelCredentialStatus(OperatorId, SiteA, ChannelKind.Telegram),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(credential.Id, result.Value.ChannelCredentialId);
        Assert.Equal(RegisteredAt, result.Value.CreatedAt);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksChannelManage_ReturnsForbidden()
    {
        var fixture = CreateFixture();
        // No grant at all - the operator has never been given channel:manage for any site.
        fixture.Credentials.Seed(ActiveCredential(SiteA, ChannelKind.Telegram, RegisteredAt));

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetChannelCredentialStatus.GetChannelCredentialStatus(OperatorId, SiteA, ChannelKind.Telegram),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    /// <summary>`23-85`/`adr/0151`'s own Scope, point 5: "23-36's live status read stops for a lapsed
    /// entitlement." This handler returning the refusal is what actually stops it - see this handler's
    /// own remarks: <c>TelegramChannelEndpoints.HandleStatusAsync</c> calls this handler first and
    /// returns its error immediately, never reaching Telegram's own <c>getMe</c>. Fails-before:
    /// reverting the entitlement check here makes this test fail with a successful (connected) status
    /// instead of a refusal, which would leave the live check running for a lapsed account.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheAccountHasNoChannelEntitlement_ReturnsChannelNotEntitled()
    {
        var fixture = CreateFixture(grantEntitlement: false);
        fixture.Permissions.Grant(OperatorId, SiteA, Permission.ChannelManage);
        fixture.Credentials.Seed(ActiveCredential(SiteA, ChannelKind.Telegram, RegisteredAt));

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetChannelCredentialStatus.GetChannelCredentialStatus(OperatorId, SiteA, ChannelKind.Telegram),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("ChannelCredential.NotEntitled", result.Error!.Value.Code);
    }

    /// <summary>
    /// `23-36`'s own demand: "one tenant's channel configuration is unreachable from another's
    /// session", demonstrated rather than asserted. The operator holds <c>channel:manage</c> for
    /// <see cref="SiteA"/> only - a permission grant scoped to a different site than the one it is
    /// checked against is worthless, the same as holding no permission at all
    /// (<c>FakePermissionChecker</c>'s own site-scoped storage mirrors the real RBAC read). If this
    /// handler, or <see cref="Abstractions.IChannelCredentialRepository.GetActiveAsync"/> underneath
    /// it, ever stopped scoping the query by <see cref="SiteId"/> - the guard this test exists to
    /// catch - this would start returning Site B's credential to an Operator who was never granted
    /// anything on Site B, through the permission check for Site A alone.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenTheOperatorHoldsChannelManageOnlyForAnotherSite_ReturnsForbiddenNotSiteBsCredential()
    {
        var fixture = CreateFixture();
        fixture.Permissions.Grant(OperatorId, SiteA, Permission.ChannelManage);
        // SiteB has an active credential this operator has no business ever seeing.
        var siteBCredential = ActiveCredential(SiteB, ChannelKind.Telegram, RegisteredAt);
        fixture.Credentials.Seed(siteBCredential);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetChannelCredentialStatus.GetChannelCredentialStatus(OperatorId, SiteB, ChannelKind.Telegram),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    /// <summary>
    /// `23-36`'s other own demand: "a stored credential never comes back out through any read path" -
    /// made observable the same way <c>RegisterChannelCredentialHandlerTests</c>'s own
    /// <c>HandleAsync_ReturnsTheWebhookSecretButNeverTheToken</c> already does for the write side, by
    /// reflecting over the result type rather than trusting a hand-read of its fields. This fails the
    /// moment anyone adds a property to <see cref="ChannelCredentialStatus"/> that could hold a
    /// secret - there is no allow-list of "safe" property names to keep in sync, only an exhaustive
    /// one of what this type is permitted to carry at all.
    /// </summary>
    [Fact]
    public void ChannelCredentialStatus_CarriesOnlyAnIdAndACreatedAtTimestamp_NeverAnythingShapedLikeASecret()
    {
        var properties = typeof(ChannelCredentialStatus).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        Assert.Equal(2, properties.Length);
        Assert.Contains(properties, p => p.Name == nameof(ChannelCredentialStatus.ChannelCredentialId) && p.PropertyType == typeof(ChannelCredentialId?));
        Assert.Contains(properties, p => p.Name == nameof(ChannelCredentialStatus.CreatedAt) && p.PropertyType == typeof(DateTimeOffset?));
    }
}
