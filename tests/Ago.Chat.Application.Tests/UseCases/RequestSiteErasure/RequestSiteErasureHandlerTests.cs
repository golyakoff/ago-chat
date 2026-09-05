using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.RequestSiteErasure;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RequestSiteErasure;

public class RequestSiteErasureHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(RequestSiteErasureHandler Handler, FakeErasureRequestRepository Erasures);

    private static Fixture CreateFixture(bool grantPermission = true, bool seedSite = true)
    {
        var erasures = new FakeErasureRequestRepository();
        if (seedSite)
        {
            erasures.SeedSite(SiteId);
        }

        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.SiteErase);
        }

        var handler = new RequestSiteErasureHandler(erasures, permissions, new FakeIdGenerator(), new FakeClock(Now));
        return new Fixture(handler, erasures);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_SetsTheErasureRequestedAtFlag()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.RequestSiteErasure.RequestSiteErasure(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(Now, fixture.Erasures.SiteErasureRequestedAt[SiteId]);
    }

    // `24-13`: the receipt this erasure will be provable by - minted the same call that sets the
    // flag, never a second one (IErasureRequestRepository's own remarks on why one statement).
    [Fact]
    public async Task HandleAsync_WhenPermitted_MintsAnErasureReceiptForTheRequestingOperator()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(new Application.UseCases.RequestSiteErasure.RequestSiteErasure(SiteId, OperatorId), CancellationToken.None);

        Assert.Equal(OperatorId, fixture.Erasures.SiteErasureRequestedBy[SiteId]);
        Assert.NotEqual(Guid.Empty, fixture.Erasures.SiteErasureRecordIds[SiteId]);
    }

    // 16-02's own idempotency contract: a second request does not push the timestamp forward - the
    // 30-day backup-completeness window is measured from the first request, not the last.
    [Fact]
    public async Task HandleAsync_CalledTwice_PreservesTheFirstRequestsTimestamp()
    {
        var fixture = CreateFixture();
        var laterClock = new FakeClock(Now.AddHours(1));
        var laterHandler = new RequestSiteErasureHandler(fixture.Erasures, MakeGrantedChecker(), new FakeIdGenerator(), laterClock);

        await fixture.Handler.HandleAsync(new Application.UseCases.RequestSiteErasure.RequestSiteErasure(SiteId, OperatorId), CancellationToken.None);
        var firstRecordId = fixture.Erasures.SiteErasureRecordIds[SiteId];
        var second = await laterHandler.HandleAsync(new Application.UseCases.RequestSiteErasure.RequestSiteErasure(SiteId, OperatorId), CancellationToken.None);

        Assert.True(second.IsSuccess);
        Assert.Equal(Now, fixture.Erasures.SiteErasureRequestedAt[SiteId]);
        // `24-13`: the second call's own freshly-minted record id is discarded, not persisted - one
        // receipt per erasure request, ever, the same "must not grow a row per attempt" idempotency
        // ErasureRecordQuery's own remarks require of the completion side.
        Assert.Equal(firstRecordId, fixture.Erasures.SiteErasureRecordIds[SiteId]);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksSiteErase_ReturnsForbidden_AndSetsNoFlag()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.RequestSiteErasure.RequestSiteErasure(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(fixture.Erasures.SiteErasureRequestedAt);
    }

    [Fact]
    public async Task HandleAsync_WhenTheSiteDoesNotExist_ReturnsSiteNotFound()
    {
        var fixture = CreateFixture(seedSite: false);

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.RequestSiteErasure.RequestSiteErasure(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.NotFound", result.Error!.Value.Code);
    }

    private static FakePermissionChecker MakeGrantedChecker()
    {
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteErase);
        return permissions;
    }
}
