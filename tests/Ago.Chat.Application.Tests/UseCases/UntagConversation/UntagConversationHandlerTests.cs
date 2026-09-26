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
        var handler = new UntagConversationHandler(readStore, tags, permissions, new FakeClock(Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.UntagConversation.UntagConversation(conversation.Id, SiteId, tag.Id, OperatorId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(await tags.GetForConversationAsync(conversation.Id, CancellationToken.None));
        // `adr/0186` S1: the real publish happens one layer down in `TagRepository` (its own remarks) -
        // this only proves the fake stand-in recorded the fact with the right ids, which is as far as a
        // handler-level test can see.
        var recorded = Assert.Single(tags.Untagged);
        Assert.Equal(conversation.Id, recorded.ConversationId);
        Assert.Equal(SiteId, recorded.SiteId);
        Assert.Equal(tag.Id, recorded.TagId);
        Assert.Equal(Now, recorded.Now);
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
        var handler = new UntagConversationHandler(readStore, tags, permissions, new FakeClock(Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.UntagConversation.UntagConversation(conversation.Id, SiteId, tag.Id, OperatorId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        // `adr/0186` S1: never applied, so nothing to publish - no analytics fact fabricated for a no-op.
        Assert.Empty(tags.Untagged);
    }
}
