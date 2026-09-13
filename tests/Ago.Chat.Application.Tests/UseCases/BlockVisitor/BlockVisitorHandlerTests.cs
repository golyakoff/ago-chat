using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.BlockVisitor;

/// <summary>`23-77`'s own Done-when: blocking reaches the visitor, indefinitely, without touching
/// `24-10`'s own per-conversation mechanism at all.</summary>
public class BlockVisitorHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        Application.UseCases.BlockVisitor.BlockVisitorHandler Handler,
        FakeConversationRepository Conversations,
        FakePermissionChecker Permissions,
        FakeVisitorRestrictionRepository Restrictions,
        Conversation Conversation);

    private static Fixture CreateHandler(bool grantPermission = true)
    {
        var conversations = new FakeConversationRepository();
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversations.Seed(conversation);

        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationBlock);
        }

        var restrictions = new FakeVisitorRestrictionRepository();
        var handler = new Application.UseCases.BlockVisitor.BlockVisitorHandler(
            conversations, restrictions, permissions, new FakeIdGenerator(), new FakeClock(Now));
        return new Fixture(handler, conversations, permissions, restrictions, conversation);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_WritesAnIndefiniteRestriction_AndDoesNotCloseTheConversation()
    {
        var fixture = CreateHandler();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.BlockVisitor.BlockVisitor(fixture.Conversation.Id, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(VisitorId, result.Value.VisitorId);
        Assert.Equal(ConversationState.Waiting, fixture.Conversation.State);
        Assert.False(fixture.Conversation.IsBlocked);

        var restriction = Assert.Single(fixture.Restrictions.Restrictions);
        Assert.Equal(VisitorRestrictionKind.Block, restriction.Kind);
        Assert.Null(restriction.ExpiresAt);
        Assert.True(await fixture.Restrictions.IsActiveAsync(SiteId, VisitorId, Now.AddYears(50), CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_WhenNotPermitted_ReturnsForbidden_WritesNothing()
    {
        var fixture = CreateHandler(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.BlockVisitor.BlockVisitor(fixture.Conversation.Id, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(fixture.Restrictions.Restrictions);
    }

    [Fact]
    public async Task HandleAsync_WhenConversationDoesNotExist_ReturnsNotFound()
    {
        var fixture = CreateHandler();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.BlockVisitor.BlockVisitor(new ConversationId(Guid.NewGuid()), OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenConversationBelongsToAnotherSite_ReturnsNotFound()
    {
        var fixture = CreateHandler();
        var otherSite = new SiteId(Guid.NewGuid());
        fixture.Permissions.Grant(OperatorId, otherSite, Permission.ConversationBlock);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.BlockVisitor.BlockVisitor(fixture.Conversation.Id, OperatorId, otherSite), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }
}
