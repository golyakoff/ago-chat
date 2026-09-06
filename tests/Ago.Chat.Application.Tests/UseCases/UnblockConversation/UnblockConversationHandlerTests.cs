using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.UnblockConversation;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.UnblockConversation;

public class UnblockConversationHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly ConversationId ConversationId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(UnblockConversationHandler Handler, FakeConversationBlockRepository Blocks);

    private static Fixture CreateFixture(bool grantPermission = true, bool seedConversation = true, bool blocked = true)
    {
        var blocks = new FakeConversationBlockRepository();
        if (seedConversation)
        {
            blocks.SeedConversation(ConversationId, SiteId, blocked);
        }

        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationBlock);
        }

        var handler = new UnblockConversationHandler(blocks, permissions, new FakeIdGenerator(), new FakeClock(Now));
        return new Fixture(handler, blocks);
    }

    [Fact]
    public async Task HandleAsync_WhenBlocked_UnblocksTheConversation_AndReturnsItsStatus()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.UnblockConversation.UnblockConversation(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ConversationId, result.Value.ConversationId);
        Assert.Equal(OperatorId, result.Value.OperatorId);
    }

    [Fact]
    public async Task HandleAsync_WhenBlocked_RecordsTheUnblockAsAnAct()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(
            new Application.UseCases.UnblockConversation.UnblockConversation(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        var record = Assert.Single(fixture.Blocks.Records);
        Assert.Equal(ConversationBlockRecordKind.Unblocked, record.Kind);
        Assert.Equal(OperatorId, record.ActorId);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksConversationBlock_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.UnblockConversation.UnblockConversation(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTheConversationDoesNotExist_ReturnsNotFound()
    {
        var fixture = CreateFixture(seedConversation: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.UnblockConversation.UnblockConversation(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenNotCurrentlyBlocked_ReturnsConversationNotBlocked()
    {
        var fixture = CreateFixture(blocked: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.UnblockConversation.UnblockConversation(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotBlocked", result.Error!.Value.Code);
        Assert.Empty(fixture.Blocks.Records);
    }
}
