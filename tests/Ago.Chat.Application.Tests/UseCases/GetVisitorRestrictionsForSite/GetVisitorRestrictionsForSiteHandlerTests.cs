using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetVisitorRestrictionsForSite;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetVisitorRestrictionsForSite;

/// <summary>`23-69`'s own Done-when: "the tenant can see how many, by whom" - the console screen's own
/// read, gated the same tenant-wide-oversight way `GetAccessRecordsForSiteHandler` already is.</summary>
public class GetVisitorRestrictionsForSiteHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_WhenPermitted_ReturnsTheSite_sOwnPage()
    {
        var restrictions = new FakeVisitorRestrictionRepository();
        await restrictions.RestrictAsync(
            SiteId, VisitorId, new OperatorId(Guid.NewGuid()), VisitorRestrictionKind.Spam, Now.AddHours(24),
            new ConversationId(Guid.NewGuid()), Guid.NewGuid(), Now, CancellationToken.None);
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        var handler = new GetVisitorRestrictionsForSiteHandler(restrictions, permissions);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetVisitorRestrictionsForSite.GetVisitorRestrictionsForSite(SiteId, OperatorId, null, null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Items);
    }

    [Fact]
    public async Task HandleAsync_WhenNotPermitted_ReturnsForbidden()
    {
        var restrictions = new FakeVisitorRestrictionRepository();
        var permissions = new FakePermissionChecker();
        var handler = new GetVisitorRestrictionsForSiteHandler(restrictions, permissions);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetVisitorRestrictionsForSite.GetVisitorRestrictionsForSite(SiteId, OperatorId, null, null),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }
}
