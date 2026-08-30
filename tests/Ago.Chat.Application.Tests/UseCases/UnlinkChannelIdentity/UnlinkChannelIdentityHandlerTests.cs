using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.UnlinkChannelIdentity;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.UnlinkChannelIdentity;

/// <summary>`14-12`/`adr/0079` decision 4: an operator without <see cref="Permission.ChannelIdentityUnlink"/>
/// cannot unlink; one who holds it (via a role the tenant defined) can.</summary>
public class UnlinkChannelIdentityHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());

    private sealed record Fixture(
        Application.UseCases.UnlinkChannelIdentity.UnlinkChannelIdentityHandler Handler,
        FakeChannelIdentityRepository Identities, ChannelIdentity Identity, FakePermissionChecker Permissions);

    private static Fixture CreateFixture(bool permitted = true)
    {
        var identities = new FakeChannelIdentityRepository();
        var identity = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), SiteId, ChannelKind.Telegram,
            new ExternalChannelAddress("tg-user-1"), VisitorId, Now);
        identities.SaveAsync(identity, CancellationToken.None).GetAwaiter().GetResult();

        var permissions = new FakePermissionChecker();
        if (permitted)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ChannelIdentityUnlink);
        }

        var handler = new Application.UseCases.UnlinkChannelIdentity.UnlinkChannelIdentityHandler(
            identities, permissions, new FakeClock(Now.AddHours(1)));
        return new Fixture(handler, identities, identity, permissions);
    }

    [Fact]
    public async Task HandleAsync_WithThePermission_UnlinksTheIdentity()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.UnlinkChannelIdentity.UnlinkChannelIdentity(OperatorId, SiteId, fixture.Identity.Id),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(fixture.Identity.Active);
        Assert.NotNull(fixture.Identity.UnlinkedAt);
    }

    [Fact]
    public async Task HandleAsync_WithoutThePermission_ReturnsForbidden_AndLeavesTheIdentityActive()
    {
        var fixture = CreateFixture(permitted: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.UnlinkChannelIdentity.UnlinkChannelIdentity(OperatorId, SiteId, fixture.Identity.Id),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.True(fixture.Identity.Active);
    }

    [Fact]
    public async Task HandleAsync_ANonExistentIdentity_ReturnsNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.UnlinkChannelIdentity.UnlinkChannelIdentity(
                OperatorId, SiteId, new ChannelIdentityId(Guid.NewGuid())),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("ChannelIdentity.NotFound", result.Error!.Value.Code);
    }

    /// <summary>Isolates the cross-tenant guard from the permission check ahead of it: the operator
    /// holds `ChannelIdentityUnlink` for the *other* site too, so a `Conversation.Forbidden` here would
    /// mean the mismatch was never actually checked - only `ChannelIdentity.NotFound` proves it was.</summary>
    [Fact]
    public async Task HandleAsync_AnIdentityOnADifferentSite_ReturnsNotFound()
    {
        var fixture = CreateFixture();
        var otherSite = new SiteId(Guid.NewGuid());
        fixture.Permissions.Grant(OperatorId, otherSite, Permission.ChannelIdentityUnlink);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.UnlinkChannelIdentity.UnlinkChannelIdentity(OperatorId, otherSite, fixture.Identity.Id),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("ChannelIdentity.NotFound", result.Error!.Value.Code);
    }

    /// <summary>`RevokeChannelCredentialHandler`'s own idempotent-retry shape, mirrored: unlinking an
    /// already-unlinked identity is a successful no-op, not an error - a double-clicked "unlink" button
    /// must not fail the second time.</summary>
    [Fact]
    public async Task HandleAsync_AnAlreadyUnlinkedIdentity_SucceedsAsANoOp()
    {
        var fixture = CreateFixture();
        await fixture.Handler.HandleAsync(
            new Application.UseCases.UnlinkChannelIdentity.UnlinkChannelIdentity(OperatorId, SiteId, fixture.Identity.Id),
            CancellationToken.None);
        var firstUnlinkedAt = fixture.Identity.UnlinkedAt;

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.UnlinkChannelIdentity.UnlinkChannelIdentity(OperatorId, SiteId, fixture.Identity.Id),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(firstUnlinkedAt, fixture.Identity.UnlinkedAt);
    }
}
