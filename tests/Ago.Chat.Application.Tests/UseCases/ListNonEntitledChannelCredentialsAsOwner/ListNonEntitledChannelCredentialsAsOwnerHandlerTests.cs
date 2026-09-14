using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.ListNonEntitledChannelCredentialsAsOwner;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ListNonEntitledChannelCredentialsAsOwner;

/// <summary>`23-85`/`adr/0151`: the read half of the owner's own walkthrough - every port faked, the
/// same shape every other owner-only handler's tests already use (no permission port to fake here:
/// this handler, like every other `RequirePlatformOwner` handler, calls none).</summary>
public class ListNonEntitledChannelCredentialsAsOwnerHandlerTests
{
    private static readonly SiteId SiteA = new(Guid.NewGuid());
    private static readonly SiteId SiteB = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        ListNonEntitledChannelCredentialsAsOwnerHandler Handler, FakeChannelCredentialRepository Credentials,
        FakeBillingOptionEntitlementProvider Entitlements, FakeModuleQuantityGrantStore Grants);

    private static Fixture CreateFixture()
    {
        var credentials = new FakeChannelCredentialRepository();
        var entitlements = new FakeBillingOptionEntitlementProvider();
        var grants = new FakeModuleQuantityGrantStore();
        var handler = new ListNonEntitledChannelCredentialsAsOwnerHandler(credentials, entitlements, grants);
        return new Fixture(handler, credentials, entitlements, grants);
    }

    private static void Entitle(Fixture fixture, SiteId siteId, ChannelKind kind)
    {
        var optionKey = ChannelEntitlementOptionKeys.For(kind);
        var moduleKey = new ModuleKey(optionKey.Value);
        fixture.Entitlements.Map(optionKey, moduleKey);
        _ = fixture.Grants.GrantAsync(siteId, moduleKey, 1, Now, CancellationToken.None);
    }

    private static ChannelCredential Seed(Fixture fixture, SiteId siteId, ChannelKind kind)
    {
        var credential = ChannelCredential.Register(
            new ChannelCredentialId(Guid.NewGuid()), siteId, kind, [1, 2, 3], [4, 5, 6], Now);
        fixture.Credentials.Seed(credential);
        return credential;
    }

    [Fact]
    public async Task HandleAsync_WithNoCredentialsAtAll_ReturnsEmpty()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.ListNonEntitledChannelCredentialsAsOwner.ListNonEntitledChannelCredentialsAsOwner(), CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task HandleAsync_AnActiveCredentialWithNoEntitlement_IsListed()
    {
        var fixture = CreateFixture();
        var credential = Seed(fixture, SiteA, ChannelKind.Telegram);

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.ListNonEntitledChannelCredentialsAsOwner.ListNonEntitledChannelCredentialsAsOwner(), CancellationToken.None);

        var row = Assert.Single(result);
        Assert.Equal(credential.Id, row.ChannelCredentialId);
        Assert.Equal(SiteA, row.SiteId);
        Assert.Equal(ChannelKind.Telegram, row.Kind);
        Assert.Equal(Now, row.CreatedAt);
    }

    [Fact]
    public async Task HandleAsync_AnActiveCredentialWithAnEntitlement_IsNotListed()
    {
        var fixture = CreateFixture();
        Seed(fixture, SiteA, ChannelKind.Telegram);
        Entitle(fixture, SiteA, ChannelKind.Telegram);

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.ListNonEntitledChannelCredentialsAsOwner.ListNonEntitledChannelCredentialsAsOwner(), CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task HandleAsync_ARevokedCredentialWithNoEntitlement_IsNotListed()
    {
        var fixture = CreateFixture();
        var credential = Seed(fixture, SiteA, ChannelKind.Telegram);
        credential.Revoke();
        fixture.Credentials.Seed(credential);

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.ListNonEntitledChannelCredentialsAsOwner.ListNonEntitledChannelCredentialsAsOwner(), CancellationToken.None);

        Assert.Empty(result);
    }

    /// <summary>`23-85`'s own per-channel-kind decision: an account entitled for Telegram is still
    /// listed for its own unentitled WhatsApp credential.</summary>
    [Fact]
    public async Task HandleAsync_EntitledForOneKindButNotAnother_ListsOnlyTheUnentitledKind()
    {
        var fixture = CreateFixture();
        Seed(fixture, SiteA, ChannelKind.Telegram);
        Entitle(fixture, SiteA, ChannelKind.Telegram);
        var whatsApp = Seed(fixture, SiteA, ChannelKind.WhatsApp);

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.ListNonEntitledChannelCredentialsAsOwner.ListNonEntitledChannelCredentialsAsOwner(), CancellationToken.None);

        var row = Assert.Single(result);
        Assert.Equal(whatsApp.Id, row.ChannelCredentialId);
        Assert.Equal(ChannelKind.WhatsApp, row.Kind);
    }

    [Fact]
    public async Task HandleAsync_SpansEveryTenant()
    {
        var fixture = CreateFixture();
        var siteACredential = Seed(fixture, SiteA, ChannelKind.Telegram);
        var siteBCredential = Seed(fixture, SiteB, ChannelKind.WhatsApp);

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.ListNonEntitledChannelCredentialsAsOwner.ListNonEntitledChannelCredentialsAsOwner(), CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.ChannelCredentialId == siteACredential.Id);
        Assert.Contains(result, r => r.ChannelCredentialId == siteBCredential.Id);
    }
}
