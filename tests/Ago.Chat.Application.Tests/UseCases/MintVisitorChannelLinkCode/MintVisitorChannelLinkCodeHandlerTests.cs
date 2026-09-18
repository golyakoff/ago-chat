using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.MintVisitorChannelLinkCode;

public class MintVisitorChannelLinkCodeHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());

    private sealed record Fixture(
        Application.UseCases.MintVisitorChannelLinkCode.MintVisitorChannelLinkCodeHandler Handler,
        FakeVisitorRepository Visitors,
        FakePendingChannelLinkRequestRepository PendingLinks);

    private static Fixture CreateFixture(
        bool visitorExists = true, string code = "482913", TimeSpan? validFor = null)
    {
        var visitors = new FakeVisitorRepository();
        if (visitorExists)
        {
            visitors.Seed(new Visitor(VisitorId, SiteId, Now));
        }

        var pendingLinks = new FakePendingChannelLinkRequestRepository();
        var handler = new Application.UseCases.MintVisitorChannelLinkCode.MintVisitorChannelLinkCodeHandler(
            visitors, pendingLinks, new FakePendingChannelLinkCodeGenerator(code),
            new PendingChannelLinkRequestOptions { ValidFor = validFor ?? TimeSpan.FromMinutes(15) },
            new FakeIdGenerator(), new FakeClock(Now));
        return new Fixture(handler, visitors, pendingLinks);
    }

    [Fact]
    public async Task HandleAsync_ForAnExistingVisitor_ReturnsTheCodeAndItsExpiry()
    {
        var fixture = CreateFixture(code: "482913", validFor: TimeSpan.FromMinutes(15));

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.MintVisitorChannelLinkCode.MintVisitorChannelLinkCode(SiteId, VisitorId, ChannelKind.Telegram),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("482913", result!.Code);
        Assert.Equal(Now + TimeSpan.FromMinutes(15), result.ExpiresAt);
    }

    /// <summary>The one fact this handler exists to establish - no conversation lookup, no permission
    /// check, the request is created directly against the caller's own <see cref="SiteId"/>/<see cref="VisitorId"/>.</summary>
    [Fact]
    public async Task HandleAsync_ForAnExistingVisitor_CreatesALiveVisitorInitiatedRequest()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(
            new Application.UseCases.MintVisitorChannelLinkCode.MintVisitorChannelLinkCode(SiteId, VisitorId, ChannelKind.Telegram),
            CancellationToken.None);

        var request = Assert.Single(fixture.PendingLinks.All);
        Assert.Equal(SiteId, request.SiteId);
        Assert.Equal(VisitorId, request.VisitorId);
        Assert.Equal(ChannelKind.Telegram, request.Kind);
        // adr/0079 decision 2's "visitor-initiated" shape - never the console-initiated one, since no
        // operator is involved in a visitor session mint/renew at all.
        Assert.Null(request.RequestedByOperatorId);
        Assert.True(request.IsLive(Now));
    }

    /// <summary>Committed immediately - a second call must see the first request already persisted,
    /// unlike `HandleLinkIdentityCommandHandler`'s own `Stage`-only path.</summary>
    [Fact]
    public async Task HandleAsync_CalledTwiceForAnExistingVisitor_CreatesTwoIndependentLiveRequests()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(
            new Application.UseCases.MintVisitorChannelLinkCode.MintVisitorChannelLinkCode(SiteId, VisitorId, ChannelKind.Telegram),
            CancellationToken.None);
        await fixture.Handler.HandleAsync(
            new Application.UseCases.MintVisitorChannelLinkCode.MintVisitorChannelLinkCode(SiteId, VisitorId, ChannelKind.Telegram),
            CancellationToken.None);

        Assert.Equal(2, fixture.PendingLinks.All.Count);
    }

    /// <summary>
    /// The graceful-degradation case this handler's own remarks name: `pending_channel_link_requests.visitor_id`
    /// is a real foreign key, and a freshly-minted visitor session mint has no persisted `Visitor` row yet -
    /// minting must return `null`, not throw and not create an orphaned row, so the caller (`AuthEndpoints`)
    /// can fall back to a plain link with no `?start=` code.
    /// </summary>
    [Fact]
    public async Task HandleAsync_ForAVisitorWithNoPersistedRowYet_ReturnsNull_AndCreatesNothing()
    {
        var fixture = CreateFixture(visitorExists: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.MintVisitorChannelLinkCode.MintVisitorChannelLinkCode(SiteId, VisitorId, ChannelKind.Telegram),
            CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(fixture.PendingLinks.All);
    }
}
