using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.BlockConversation;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.BlockConversation;

public class BlockConversationHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly SiteId OtherSiteId = new(Guid.NewGuid());
    private static readonly ConversationId ConversationId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(BlockConversationHandler Handler, FakeConversationBlockRepository Blocks);

    private static Fixture CreateFixture(bool grantPermission = true, bool seedConversation = true, bool alreadyBlocked = false)
    {
        var blocks = new FakeConversationBlockRepository();
        if (seedConversation)
        {
            blocks.SeedConversation(ConversationId, SiteId, alreadyBlocked);
        }

        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationBlock);
        }

        var handler = new BlockConversationHandler(blocks, permissions, new FakeIdGenerator(), new FakeClock(Now));
        return new Fixture(handler, blocks);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_BlocksTheConversation_AndReturnsItsStatus()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.BlockConversation.BlockConversation(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ConversationId, result.Value.ConversationId);
        Assert.Equal(Now, result.Value.OccurredAt);
        Assert.Equal(OperatorId, result.Value.OperatorId);
    }

    // `24-10`: "both acts are recorded" - the block itself writes its own act to the audit trail, not
    // only the current-state flag.
    [Fact]
    public async Task HandleAsync_WhenPermitted_RecordsTheBlockAsAnAct()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(
            new Application.UseCases.BlockConversation.BlockConversation(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        var record = Assert.Single(fixture.Blocks.Records);
        Assert.Equal(ConversationBlockRecordKind.Blocked, record.Kind);
        Assert.Equal(OperatorId, record.ActorId);
        Assert.Equal(Now, record.OccurredAt);
        Assert.NotEqual(Guid.Empty, record.RecordId);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksConversationBlock_ReturnsForbidden_AndBlocksNothing()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.BlockConversation.BlockConversation(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(fixture.Blocks.Records);
    }

    [Fact]
    public async Task HandleAsync_WhenTheConversationDoesNotExist_ReturnsNotFound()
    {
        var fixture = CreateFixture(seedConversation: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.BlockConversation.BlockConversation(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    // The same not-found-not-forbidden cross-tenant guard every other per-conversation check in this
    // codebase makes.
    [Fact]
    public async Task HandleAsync_WhenTheConversationBelongsToADifferentSite_ReturnsNotFound_NotForbidden()
    {
        var blocks = new FakeConversationBlockRepository();
        blocks.SeedConversation(ConversationId, OtherSiteId);
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationBlock);
        var handler = new BlockConversationHandler(blocks, permissions, new FakeIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.BlockConversation.BlockConversation(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenAlreadyBlocked_ReturnsConversationAlreadyBlocked()
    {
        var fixture = CreateFixture(alreadyBlocked: true);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.BlockConversation.BlockConversation(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.AlreadyBlocked", result.Error!.Value.Code);
        Assert.Empty(fixture.Blocks.Records);
    }
}
