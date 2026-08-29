using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetConversationTags;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetConversationTags;

public class GetConversationTagsHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_WhenPermitted_ReturnsEveryTagAppliedToTheConversation()
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        var readStore = new FakeConversationReadStore();
        readStore.Seed(conversation);
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        var tags = new FakeTagRepository();
        var tag = Tag.Create(new TagId(Guid.NewGuid()), SiteId, "VIP", Now);
        tags.Seed(tag);
        tags.SeedAssociation(conversation.Id, tag.Id);
        var handler = new GetConversationTagsHandler(readStore, tags, permissions);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetConversationTags.GetConversationTags(conversation.Id, SiteId, OperatorId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(["VIP"], result.Value.Select(t => t.Name));
    }

    [Fact]
    public async Task HandleAsync_UnknownConversation_ReturnsNotFound()
    {
        var readStore = new FakeConversationReadStore();
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        var handler = new GetConversationTagsHandler(readStore, new FakeTagRepository(), permissions);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetConversationTags.GetConversationTags(new ConversationId(Guid.NewGuid()), SiteId, OperatorId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }
}
