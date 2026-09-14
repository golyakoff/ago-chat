using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.DisconnectNonEntitledChannelCredentialsAsOwner;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.DisconnectNonEntitledChannelCredentialsAsOwner;

/// <summary>`23-85`/`adr/0151`: the write half of the owner's own walkthrough. This is the mechanism
/// the backlog item's own hard requirement is about - proven here against fakes, with fault injection
/// (a stale/wrong id, an already-inactive row, a row that became entitled again since the list was
/// read), exactly as thoroughly as every other write in this codebase, precisely because the item
/// itself refuses to let it run against anything real without the author's own hand on the
/// trigger.</summary>
public class DisconnectNonEntitledChannelCredentialsAsOwnerHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        DisconnectNonEntitledChannelCredentialsAsOwnerHandler Handler, FakeChannelCredentialRepository Credentials,
        FakeBillingOptionEntitlementProvider Entitlements, FakeModuleQuantityGrantStore Grants);

    private static Fixture CreateFixture()
    {
        var credentials = new FakeChannelCredentialRepository();
        var entitlements = new FakeBillingOptionEntitlementProvider();
        var grants = new FakeModuleQuantityGrantStore();
        var handler = new DisconnectNonEntitledChannelCredentialsAsOwnerHandler(credentials, entitlements, grants);
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
            new ChannelCredentialId(Guid.NewGuid()), siteId, kind, [1, 2, 3], [4, 5, 6], Now,
            refreshTokenCiphertext: kind == ChannelKind.Avito ? [9, 9, 9] : null);
        fixture.Credentials.Seed(credential);
        return credential;
    }

    /// <summary>The item's own Done-when box: "accounts already connected without an entitlement are
    /// disconnected and their channel credentials cleaned up."</summary>
    [Fact]
    public async Task HandleAsync_ForAnUnentitledActiveCredential_DisconnectsAndClearsTheToken()
    {
        var fixture = CreateFixture();
        var credential = Seed(fixture, SiteId, ChannelKind.Telegram);

        var outcomes = await fixture.Handler.HandleAsync(
            new Application.UseCases.DisconnectNonEntitledChannelCredentialsAsOwner.DisconnectNonEntitledChannelCredentialsAsOwner([credential.Id]), CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(credential.Id, outcome.ChannelCredentialId);
        Assert.Equal(ChannelCredentialDisconnectStatus.Disconnected, outcome.Status);

        var stored = await fixture.Credentials.GetByIdAsync(credential.Id, CancellationToken.None);
        Assert.False(stored!.Active);
        Assert.Empty(stored.TokenCiphertext);
    }

    [Fact]
    public async Task HandleAsync_ForAnUnentitledCredentialWithARefreshToken_ClearsThatToo()
    {
        var fixture = CreateFixture();
        var credential = Seed(fixture, SiteId, ChannelKind.Avito);

        await fixture.Handler.HandleAsync(new Application.UseCases.DisconnectNonEntitledChannelCredentialsAsOwner.DisconnectNonEntitledChannelCredentialsAsOwner([credential.Id]), CancellationToken.None);

        var stored = await fixture.Credentials.GetByIdAsync(credential.Id, CancellationToken.None);
        Assert.NotNull(stored!.RefreshTokenCiphertext);
        Assert.Empty(stored.RefreshTokenCiphertext);
    }

    /// <summary>Fault injection: an id that names no row at all - a stale list, or a typo.</summary>
    [Fact]
    public async Task HandleAsync_ForAnIdThatDoesNotExist_ReturnsNotFound_AndTouchesNothing()
    {
        var fixture = CreateFixture();
        var unknownId = new ChannelCredentialId(Guid.NewGuid());

        var outcomes = await fixture.Handler.HandleAsync(
            new Application.UseCases.DisconnectNonEntitledChannelCredentialsAsOwner.DisconnectNonEntitledChannelCredentialsAsOwner([unknownId]), CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(ChannelCredentialDisconnectStatus.NotFound, outcome.Status);
    }

    /// <summary>Fault injection: the row was already revoked (by the tenant themselves, or by an
    /// earlier run of this same command) - a no-op, not an error, the identical idempotent-retry shape
    /// every revoke path in this codebase already has.</summary>
    [Fact]
    public async Task HandleAsync_ForAnAlreadyInactiveCredential_ReturnsAlreadyInactive_AndDoesNotThrow()
    {
        var fixture = CreateFixture();
        var credential = Seed(fixture, SiteId, ChannelKind.Telegram);
        credential.Revoke();
        fixture.Credentials.Seed(credential);

        var outcomes = await fixture.Handler.HandleAsync(
            new Application.UseCases.DisconnectNonEntitledChannelCredentialsAsOwner.DisconnectNonEntitledChannelCredentialsAsOwner([credential.Id]), CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(ChannelCredentialDisconnectStatus.AlreadyInactive, outcome.Status);
    }

    /// <summary>Fault injection, and the one this handler's own remarks call out specifically: the
    /// account became entitled again in the gap between the owner reading the list and acting on it
    /// (paid, or was granted by hand). Re-checked at execution time, not trusted from the caller's own
    /// stale selection - the row is left connected.</summary>
    [Fact]
    public async Task HandleAsync_ForACredentialThatBecameEntitledSinceTheListWasRead_ReturnsStillEntitled_AndLeavesItConnected()
    {
        var fixture = CreateFixture();
        var credential = Seed(fixture, SiteId, ChannelKind.Telegram);
        Entitle(fixture, SiteId, ChannelKind.Telegram);

        var outcomes = await fixture.Handler.HandleAsync(
            new Application.UseCases.DisconnectNonEntitledChannelCredentialsAsOwner.DisconnectNonEntitledChannelCredentialsAsOwner([credential.Id]), CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(ChannelCredentialDisconnectStatus.StillEntitled, outcome.Status);
        var stored = await fixture.Credentials.GetByIdAsync(credential.Id, CancellationToken.None);
        Assert.True(stored!.Active);
        Assert.Equal(new byte[] { 1, 2, 3 }, stored.TokenCiphertext);
    }

    /// <summary>`23-85`'s own per-channel-kind decision, exercised at the write side too: entitled for
    /// Telegram does not protect an unrelated WhatsApp credential on the same site from disconnect.
    /// </summary>
    [Fact]
    public async Task HandleAsync_EntitledForOneKindOnly_StillDisconnectsAnotherKindsCredential()
    {
        var fixture = CreateFixture();
        Entitle(fixture, SiteId, ChannelKind.Telegram);
        var whatsApp = Seed(fixture, SiteId, ChannelKind.WhatsApp);

        var outcomes = await fixture.Handler.HandleAsync(
            new Application.UseCases.DisconnectNonEntitledChannelCredentialsAsOwner.DisconnectNonEntitledChannelCredentialsAsOwner([whatsApp.Id]), CancellationToken.None);

        Assert.Equal(ChannelCredentialDisconnectStatus.Disconnected, Assert.Single(outcomes).Status);
    }

    /// <summary>Several ids in one request, mixed outcomes - the batch does not stop at the first
    /// non-`Disconnected` result.</summary>
    [Fact]
    public async Task HandleAsync_WithAMixOfIds_ReportsEachOutcomeIndependently()
    {
        var fixture = CreateFixture();
        var toDisconnect = Seed(fixture, SiteId, ChannelKind.Telegram);
        var alreadyEntitled = Seed(fixture, SiteId, ChannelKind.WhatsApp);
        Entitle(fixture, SiteId, ChannelKind.WhatsApp);
        var unknownId = new ChannelCredentialId(Guid.NewGuid());

        var outcomes = await fixture.Handler.HandleAsync(
            new Application.UseCases.DisconnectNonEntitledChannelCredentialsAsOwner.DisconnectNonEntitledChannelCredentialsAsOwner([toDisconnect.Id, alreadyEntitled.Id, unknownId]),
            CancellationToken.None);

        Assert.Equal(3, outcomes.Count);
        Assert.Equal(ChannelCredentialDisconnectStatus.Disconnected, outcomes.Single(o => o.ChannelCredentialId == toDisconnect.Id).Status);
        Assert.Equal(ChannelCredentialDisconnectStatus.StillEntitled, outcomes.Single(o => o.ChannelCredentialId == alreadyEntitled.Id).Status);
        Assert.Equal(ChannelCredentialDisconnectStatus.NotFound, outcomes.Single(o => o.ChannelCredentialId == unknownId).Status);
    }
}
