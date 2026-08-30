using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.UnlinkChannelIdentityAsOwner;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.UnlinkChannelIdentityAsOwner;

/// <summary>`14-12`/`adr/0079`: the platform owner's own unconditional unlink - no
/// <see cref="Permission.ChannelIdentityUnlink"/> check exists in this handler at all (there is no
/// <see cref="OperatorId"/> to check it against), the same "the policy already decided" shape
/// <c>ListSitesForOwnerHandler</c>'s own remarks describe. What this handler still does check, and what
/// these tests prove, is the site-scoped guard against a mismatched id in the URL.</summary>
public class UnlinkChannelIdentityAsOwnerHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());

    private sealed record Fixture(
        Application.UseCases.UnlinkChannelIdentityAsOwner.UnlinkChannelIdentityAsOwnerHandler Handler,
        FakeChannelIdentityRepository Identities, ChannelIdentity Identity);

    private static Fixture CreateFixture()
    {
        var identities = new FakeChannelIdentityRepository();
        var identity = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), SiteId, ChannelKind.Telegram,
            new ExternalChannelAddress("tg-user-1"), VisitorId, Now);
        identities.SaveAsync(identity, CancellationToken.None).GetAwaiter().GetResult();

        var handler = new Application.UseCases.UnlinkChannelIdentityAsOwner.UnlinkChannelIdentityAsOwnerHandler(
            identities, new FakeClock(Now.AddHours(1)));
        return new Fixture(handler, identities, identity);
    }

    [Fact]
    public async Task HandleAsync_UnlinksTheIdentity_WithNoPermissionCheckOfAnyKind()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.UnlinkChannelIdentityAsOwner.UnlinkChannelIdentityAsOwner(SiteId, fixture.Identity.Id),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(fixture.Identity.Active);
    }

    [Fact]
    public async Task HandleAsync_ANonExistentIdentity_ReturnsNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.UnlinkChannelIdentityAsOwner.UnlinkChannelIdentityAsOwner(
                SiteId, new ChannelIdentityId(Guid.NewGuid())),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("ChannelIdentity.NotFound", result.Error!.Value.Code);
    }

    /// <summary>The URL's own site id must actually agree with the row's real site - this handler's own
    /// remarks on why "cross-tenant on purpose" is a property of the route, not license to skip
    /// validating the two ids agree.</summary>
    [Fact]
    public async Task HandleAsync_ASiteIdThatDoesNotMatchTheIdentitysRealSite_ReturnsNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.UnlinkChannelIdentityAsOwner.UnlinkChannelIdentityAsOwner(
                new SiteId(Guid.NewGuid()), fixture.Identity.Id),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("ChannelIdentity.NotFound", result.Error!.Value.Code);
        Assert.True(fixture.Identity.Active);
    }

    [Fact]
    public async Task HandleAsync_AnAlreadyUnlinkedIdentity_SucceedsAsANoOp()
    {
        var fixture = CreateFixture();
        await fixture.Handler.HandleAsync(
            new Application.UseCases.UnlinkChannelIdentityAsOwner.UnlinkChannelIdentityAsOwner(SiteId, fixture.Identity.Id),
            CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.UnlinkChannelIdentityAsOwner.UnlinkChannelIdentityAsOwner(SiteId, fixture.Identity.Id),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
    }
}
