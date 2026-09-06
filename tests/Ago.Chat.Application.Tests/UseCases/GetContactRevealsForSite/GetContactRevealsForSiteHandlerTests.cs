using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetContactRevealsForSite;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetContactRevealsForSite;

/// <summary>`23-11`'s own Done-when: "the tenant can read the reveal record" - proven here as an
/// ordinary permission gate plus tenant isolation, the same shape `GetAccessRecordsForSiteHandlerTests`
/// already establishes for its own sibling audit read.</summary>
public class GetContactRevealsForSiteHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly SiteId OtherSiteId = new(Guid.NewGuid());
    private static readonly OperatorId AdminOperatorId = new(Guid.NewGuid());
    private static readonly OperatorId UnprivilegedOperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static (GetContactRevealsForSiteHandler Handler, FakeContactRevealRepository Reveals) CreateFixture()
    {
        var permissions = new FakePermissionChecker();
        permissions.Grant(AdminOperatorId, SiteId, Permission.SiteConfigure);
        var reveals = new FakeContactRevealRepository();
        return (new GetContactRevealsForSiteHandler(reveals, permissions), reveals);
    }

    [Fact]
    public async Task HandleAsync_WithoutSiteConfigurePermission_ReturnsForbidden()
    {
        var (handler, _) = CreateFixture();

        var result = await handler.HandleAsync(
            new Application.UseCases.GetContactRevealsForSite.GetContactRevealsForSite(SiteId, UnprivilegedOperatorId, null, null),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    /// <summary>An operator who can reveal (`ConversationRead`) but does not also hold `SiteConfigure`
    /// cannot read the whole tenant's own reveal history - the audit view is a wider read than the
    /// action it audits.</summary>
    [Fact]
    public async Task HandleAsync_OperatorWhoCanRevealButLacksSiteConfigure_ReturnsForbidden()
    {
        var permissions = new FakePermissionChecker();
        permissions.Grant(AdminOperatorId, SiteId, Permission.SiteConfigure);
        permissions.Grant(UnprivilegedOperatorId, SiteId, Permission.ConversationRead);
        var reveals = new FakeContactRevealRepository();
        var handler = new GetContactRevealsForSiteHandler(reveals, permissions);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetContactRevealsForSite.GetContactRevealsForSite(SiteId, UnprivilegedOperatorId, null, null),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WithPermission_ReturnsOnlyThisSitesOwnReveals_NeverAnotherSites()
    {
        var (handler, reveals) = CreateFixture();
        await reveals.RecordAsync(
            new ContactRevealToWrite(Guid.NewGuid(), Now, SiteId, Guid.NewGuid(), Guid.NewGuid(), AdminOperatorId, "ConsoleContactPanel"),
            CancellationToken.None);
        await reveals.RecordAsync(
            new ContactRevealToWrite(
                Guid.NewGuid(), Now, OtherSiteId, Guid.NewGuid(), Guid.NewGuid(), new OperatorId(Guid.NewGuid()), "ConsoleContactPanel"),
            CancellationToken.None);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetContactRevealsForSite.GetContactRevealsForSite(SiteId, AdminOperatorId, null, null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value.Items);
        Assert.Equal(AdminOperatorId.Value, item.OperatorId);
    }
}
