using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.SendTeamMessage;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RemoveTeamMessage;

/// <summary>
/// `23-33`: the one decision this handler makes - who may remove - plus the idempotency and
/// info-hiding shapes it borrows from <c>DeleteAttachmentHandler</c> (its own remarks state why).
/// </summary>
public class RemoveTeamMessageHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly SiteId OtherSiteId = new(Guid.NewGuid());
    private static readonly OperatorId Owner = new(Guid.NewGuid());
    private static readonly OperatorId Colleague = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static (
        Application.UseCases.RemoveTeamMessage.RemoveTeamMessageHandler Handler,
        FakePermissionChecker Permissions,
        FakeTeamChatRepository Repository,
        FakeClock Clock)
        CreateHandler()
    {
        var permissions = new FakePermissionChecker();
        var repository = new FakeTeamChatRepository();
        var clock = new FakeClock(Now);
        var handler = new Application.UseCases.RemoveTeamMessage.RemoveTeamMessageHandler(
            repository, permissions, clock, new FakeIdGenerator());
        return (handler, permissions, repository, clock);
    }

    /// <summary>A real message this site already has, posted by whichever operator a given test
    /// needs - the fixture every test below removes.</summary>
    private static async Task<TeamMessage> SeedMessageAsync(FakeTeamChatRepository repository, SiteId siteId, OperatorId author)
    {
        var sendHandler = new SendTeamMessageHandler(
            repository, new FakePermissionChecker(), new FakeClock(Now), new FakeIdGenerator());
        var sent = await sendHandler.HandleAsync(
            new Application.UseCases.SendTeamMessage.SendTeamMessage(siteId, author, "hello team"), CancellationToken.None);
        return sent.Value;
    }

    [Fact]
    public async Task HandleAsync_WhenTheCallerHoldsSiteManageOperators_RemovesTheMessage()
    {
        var (handler, permissions, repository, _) = CreateHandler();
        var message = await SeedMessageAsync(repository, SiteId, Colleague);
        permissions.Grant(Owner, SiteId, Permission.SiteManageOperators);

        var result = await handler.HandleAsync(
            new Application.UseCases.RemoveTeamMessage.RemoveTeamMessage(SiteId, Owner, message.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(Now, result.Value.RemovedAt);
        var removal = Assert.Single(repository.Removed);
        Assert.Equal(message.Id, removal.TeamMessageId);
        Assert.Equal(Owner, removal.RemovedBy);
    }

    /// <summary>The permission this handler checks is a real capability, not the message's own stored
    /// admin label - a colleague who never sent this message but does hold the permission may still
    /// remove it, and the sender's own <see cref="TeamMessage.AuthorIsAdmin"/> plays no part.</summary>
    [Fact]
    public async Task HandleAsync_RemoverNeedNotBeTheAuthor()
    {
        var (handler, permissions, repository, _) = CreateHandler();
        var message = await SeedMessageAsync(repository, SiteId, Colleague);
        permissions.Grant(Owner, SiteId, Permission.SiteManageOperators);

        var result = await handler.HandleAsync(
            new Application.UseCases.RemoveTeamMessage.RemoveTeamMessage(SiteId, Owner, message.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task HandleAsync_WhenTheCallerDoesNotHoldSiteManageOperators_ReturnsForbidden_WithoutRemoving()
    {
        var (handler, _, repository, _) = CreateHandler();
        var message = await SeedMessageAsync(repository, SiteId, Colleague);
        // No permission granted at all - an ordinary operator seat, including the message's own author.

        var result = await handler.HandleAsync(
            new Application.UseCases.RemoveTeamMessage.RemoveTeamMessage(SiteId, Colleague, message.Id), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("TeamChat.Forbidden", result.Error!.Value.Code);
        Assert.Empty(repository.Removed);
        Assert.Null(message.RemovedAt);
    }

    /// <summary>The same "checks exactly the one permission, not the Admin role in general" pin
    /// `SendTeamMessageHandlerTests` already establishes for the send path's own label check.</summary>
    [Fact]
    public async Task HandleAsync_ChecksExactlySiteManageOperators_NotAnyOtherAdminPermission()
    {
        var (handler, permissions, repository, _) = CreateHandler();
        var message = await SeedMessageAsync(repository, SiteId, Colleague);
        permissions.Grant(Owner, SiteId, Permission.SiteConfigure);
        permissions.Grant(Owner, SiteId, Permission.SiteErase);

        var result = await handler.HandleAsync(
            new Application.UseCases.RemoveTeamMessage.RemoveTeamMessage(SiteId, Owner, message.Id), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("TeamChat.Forbidden", result.Error!.Value.Code);
    }

    /// <summary>The info-hiding shape <c>DeleteAttachmentHandler</c>'s own remarks establish: a
    /// message belonging to another site reads identically to one that does not exist at all.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheMessageBelongsToAnotherSite_ReturnsNotFound_WithoutRemoving()
    {
        var (handler, permissions, repository, _) = CreateHandler();
        var message = await SeedMessageAsync(repository, OtherSiteId, Colleague);
        permissions.Grant(Owner, SiteId, Permission.SiteManageOperators);

        var result = await handler.HandleAsync(
            new Application.UseCases.RemoveTeamMessage.RemoveTeamMessage(SiteId, Owner, message.Id), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("TeamChat.NotFound", result.Error!.Value.Code);
        Assert.Empty(repository.Removed);
    }

    [Fact]
    public async Task HandleAsync_WhenTheMessageDoesNotExist_ReturnsNotFound()
    {
        var (handler, permissions, _, _) = CreateHandler();
        permissions.Grant(Owner, SiteId, Permission.SiteManageOperators);

        var result = await handler.HandleAsync(
            new Application.UseCases.RemoveTeamMessage.RemoveTeamMessage(SiteId, Owner, new TeamMessageId(Guid.NewGuid())),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("TeamChat.NotFound", result.Error!.Value.Code);
    }

    /// <summary>The same "tolerate already-gone" idempotency <c>DeleteAttachmentHandler</c>'s own
    /// remarks give for a retried delete - a double-click, or a retry after a dropped response to an
    /// already-successful call, must succeed quietly and must not write a second removal record.</summary>
    [Fact]
    public async Task HandleAsync_WhenAlreadyRemoved_SucceedsWithoutWritingASecondRemoval()
    {
        var (handler, permissions, repository, clock) = CreateHandler();
        var message = await SeedMessageAsync(repository, SiteId, Colleague);
        permissions.Grant(Owner, SiteId, Permission.SiteManageOperators);

        var first = await handler.HandleAsync(
            new Application.UseCases.RemoveTeamMessage.RemoveTeamMessage(SiteId, Owner, message.Id), CancellationToken.None);
        Assert.True(first.IsSuccess);

        clock.UtcNow = Now.AddMinutes(1);
        var retry = await handler.HandleAsync(
            new Application.UseCases.RemoveTeamMessage.RemoveTeamMessage(SiteId, Owner, message.Id), CancellationToken.None);

        Assert.True(retry.IsSuccess);
        Assert.Equal(Now, retry.Value.RemovedAt); // the first removal's own timestamp, not the retry's
        Assert.Single(repository.Removed); // not two
    }
}
