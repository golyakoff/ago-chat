using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.SendTeamMessage;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.SendTeamMessage;

/// <summary>
/// `23-32`: the one decision this handler makes - the admin label - plus the ordinary body-shape
/// check every send path in this product repeats. There is no permission-to-send gate to test here at
/// all (see <see cref="Ago.Chat.Application.UseCases.SendTeamMessage.SendTeamMessage"/>'s own remarks): every
/// operator of the site may post, unconditionally.
/// </summary>
public class SendTeamMessageHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static (SendTeamMessageHandler Handler, FakePermissionChecker Permissions, FakeTeamChatRepository Repository)
        CreateHandler()
    {
        var permissions = new FakePermissionChecker();
        var repository = new FakeTeamChatRepository();
        var handler = new SendTeamMessageHandler(repository, permissions, new FakeClock(Now), new FakeIdGenerator());
        return (handler, permissions, repository);
    }

    [Fact]
    public async Task HandleAsync_WhenTheAuthorHoldsSiteManageOperators_LabelsTheMessageAsAdmin()
    {
        var (handler, permissions, repository) = CreateHandler();
        permissions.Grant(OperatorId, SiteId, Permission.SiteManageOperators);

        var result = await handler.HandleAsync(
            new Application.UseCases.SendTeamMessage.SendTeamMessage(SiteId, OperatorId, "I'm taking the angry one"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.AuthorIsAdmin);
        var posted = Assert.Single(repository.Posted);
        Assert.True(posted.AuthorIsAdmin);
    }

    [Fact]
    public async Task HandleAsync_WhenTheAuthorDoesNotHoldSiteManageOperators_DoesNotLabelTheMessageAsAdmin()
    {
        var (handler, _, repository) = CreateHandler();
        // No permission granted at all - an ordinary operator seat.

        var result = await handler.HandleAsync(
            new Application.UseCases.SendTeamMessage.SendTeamMessage(SiteId, OperatorId, "on it"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.AuthorIsAdmin);
        Assert.False(Assert.Single(repository.Posted).AuthorIsAdmin);
    }

    /// <summary>`5-08`'s Admin role also holds `site:configure`/`site:erase`, never only
    /// `SiteManageOperators` in isolation in production - but this handler's own decision is a single
    /// permission check, not "holds the Admin role", so a caller with only the narrower grant must
    /// label the same way a real Admin does. Pins the handler to the permission it actually checks.</summary>
    [Fact]
    public async Task HandleAsync_ChecksExactlySiteManageOperators_NotAnyOtherAdminPermission()
    {
        var (handler, permissions, repository) = CreateHandler();
        permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        permissions.Grant(OperatorId, SiteId, Permission.SiteErase);

        var result = await handler.HandleAsync(
            new Application.UseCases.SendTeamMessage.SendTeamMessage(SiteId, OperatorId, "not actually an admin label"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.AuthorIsAdmin);
        Assert.False(Assert.Single(repository.Posted).AuthorIsAdmin);
    }

    [Fact]
    public async Task HandleAsync_WhenTheBodyIsBlank_ReturnsInvalidBody_WithoutPosting()
    {
        var (handler, _, repository) = CreateHandler();

        var result = await handler.HandleAsync(
            new Application.UseCases.SendTeamMessage.SendTeamMessage(SiteId, OperatorId, "   "), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("TeamChat.InvalidBody", result.Error!.Value.Code);
        Assert.Empty(repository.Posted);
    }

    [Fact]
    public async Task HandleAsync_PostsWithTheCallersOwnSiteAndAuthor_NeverALookup()
    {
        var (handler, _, repository) = CreateHandler();

        await handler.HandleAsync(
            new Application.UseCases.SendTeamMessage.SendTeamMessage(SiteId, OperatorId, "hello team"), CancellationToken.None);

        var posted = Assert.Single(repository.Posted);
        Assert.Equal(SiteId, posted.SiteId);
        Assert.Equal(OperatorId, posted.AuthorOperatorId);
        Assert.Equal("hello team", posted.Body.Value);
        Assert.Equal(Now, posted.CreatedAt);
    }
}
