using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetSuspensionStatusForSite;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetSuspensionStatusForSite;

/// <summary>
/// `25-70`: the handler-level half of the tenant's own suspension read - the same "permission check
/// tested here, not merely asserted by inspection" shape
/// <c>ListEnabledModulesForSite.ListEnabledModulesForSiteHandlerTests</c> already gives its own sibling
/// read, extended with the "since when" case that read has no equivalent of.
/// </summary>
public class GetSuspensionStatusForSiteHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly SiteId OtherSiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());

    private static (GetSuspensionStatusForSiteHandler Handler, FakeSiteSuspensionReadStore ReadStore) CreateFixture(
        bool grantPermission = true)
    {
        var readStore = new FakeSiteSuspensionReadStore();
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        }

        return (new GetSuspensionStatusForSiteHandler(readStore, permissions, new FakeClock(Now)), readStore);
    }

    [Fact]
    public async Task HandleAsync_ForASuspendedSite_ReturnsSuspendedSinceAndUntil()
    {
        var (handler, readStore) = CreateFixture();
        var since = Now.AddHours(-2);
        var until = Now.AddHours(1);
        readStore.Suspend(SiteId, until, since);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetSuspensionStatusForSite.GetSuspensionStatusForSite(OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsSuspended);
        Assert.Equal(since, result.Value.Since);
        Assert.Equal(until, result.Value.Until);
    }

    [Fact]
    public async Task HandleAsync_ForASiteThatIsNotSuspended_ReturnsNotSuspendedWithNoDates()
    {
        var (handler, _) = CreateFixture();

        var result = await handler.HandleAsync(
            new Application.UseCases.GetSuspensionStatusForSite.GetSuspensionStatusForSite(OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.IsSuspended);
        Assert.Null(result.Value.Since);
        Assert.Null(result.Value.Until);
    }

    [Fact]
    public async Task HandleAsync_ForASiteWhoseSuspensionHasAlreadyPassed_ReturnsNotSuspended()
    {
        var (handler, readStore) = CreateFixture();
        readStore.Suspend(SiteId, Now.AddMinutes(-1), Now.AddHours(-2));

        var result = await handler.HandleAsync(
            new Application.UseCases.GetSuspensionStatusForSite.GetSuspensionStatusForSite(OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.IsSuspended);
    }

    // `25-70`'s own tenant-scoping requirement: an operator holding no permission on this site - the
    // shape a caller naming another tenant's siteId is in - is refused rather than handed the state.
    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksSiteConfigureOnThisSite_ReturnsForbidden()
    {
        var (handler, readStore) = CreateFixture(grantPermission: false);
        readStore.Suspend(SiteId, Now.AddHours(1), Now.AddHours(-1));

        var result = await handler.HandleAsync(
            new Application.UseCases.GetSuspensionStatusForSite.GetSuspensionStatusForSite(OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    // The operator holds SiteConfigure on their own site, but the caller asks about a different one
    // (SiteId, not OtherSiteId, is granted) - proves the permission check is scoped to the SiteId the
    // query names, not merely to "this operator holds SiteConfigure somewhere".
    [Fact]
    public async Task HandleAsync_WhenTheOperatorHoldsSiteConfigureOnlyOnAnotherSite_ReturnsForbidden()
    {
        var (handler, readStore) = CreateFixture();
        readStore.Suspend(OtherSiteId, Now.AddHours(1), Now.AddHours(-1));

        var result = await handler.HandleAsync(
            new Application.UseCases.GetSuspensionStatusForSite.GetSuspensionStatusForSite(OperatorId, OtherSiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }
}
