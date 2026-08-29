using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.UntagConversation;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.UntagConversation;

public class UntagConversationHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_WhenPermitted_RemovesTheTag()
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        var readStore = new FakeConversationReadStore();
        readStore.Seed(conversation);
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationTag);
        var tags = new FakeTagRepository();
        var tag = Tag.Create(new TagId(Guid.NewGuid()), SiteId, "VIP", Now);
        tags.Seed(tag);
        tags.SeedAssociation(conversation.Id, tag.Id);
        var handler = new UntagConversationHandler(readStore, tags, permissions);

        var result = await handler.HandleAsync(
            new Application.UseCases.UntagConversation.UntagConversation(conversation.Id, SiteId, tag.Id, OperatorId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(await tags.GetForConversationAsync(conversation.Id, CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_NeverApplied_IsANoOp()
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        var readStore = new FakeConversationReadStore();
        readStore.Seed(conversation);
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationTag);
        var tags = new FakeTagRepository();
        var tag = Tag.Create(new TagId(Guid.NewGuid()), SiteId, "VIP", Now);
        tags.Seed(tag);
        var handler = new UntagConversationHandler(readStore, tags, permissions);

        var result = await handler.HandleAsync(
            new Application.UseCases.UntagConversation.UntagConversation(conversation.Id, SiteId, tag.Id, OperatorId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
    }
}
