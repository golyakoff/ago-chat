using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetConversationById;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetConversationById;

public class GetConversationByIdHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly ConversationId ConversationId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(GetConversationByIdHandler Handler);

    private static Fixture CreateFixture(bool grantPermission = true, bool seedConversation = true)
    {
        var readStore = new FakeConversationReadStore();
        if (seedConversation)
        {
            readStore.Seed(Conversation.Start(ConversationId, SiteId, VisitorId, Now));
        }

        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationErase);
        }

        return new Fixture(new GetConversationByIdHandler(readStore, permissions));
    }

    [Fact]
    public async Task HandleAsync_WhenPermittedAndFound_ReturnsTheConversation()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetConversationById.GetConversationById(ConversationId, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ConversationId, result.Value.Id);
        Assert.Equal(VisitorId, result.Value.VisitorId);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksConversationErase_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetConversationById.GetConversationById(ConversationId, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    // The completion-poll's whole point: once ConversationErasureJob has deleted the row, this handler
    // must answer NotFound, not throw and not return a stale row.
    [Fact]
    public async Task HandleAsync_WhenTheConversationDoesNotExist_ReturnsNotFound()
    {
        var fixture = CreateFixture(seedConversation: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetConversationById.GetConversationById(ConversationId, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }
}
