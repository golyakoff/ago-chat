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
        // `19-02`: SeedAssociation's own default is TagSource.Operator - proves the wire DTO carries
        // the CLR member name through unchanged (ConversationTagDto's own remarks).
        Assert.Equal(["Operator"], result.Value.Select(t => t.Source));
    }

    /// <summary>`19-02`'s own Done-when: an AI-applied tag is distinguishable from an operator-applied
    /// one at the data level - this is the read-side half, proving <see cref="ConversationTagDto.Source"/>
    /// actually reflects each association's own <see cref="TagSource"/> rather than a hardcoded value.
    /// The console-rendered half of the same Done-when is `ago-console`'s own component test.</summary>
    [Fact]
    public async Task HandleAsync_DistinguishesAiAppliedTagsFromOperatorAppliedOnes()
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        var readStore = new FakeConversationReadStore();
        readStore.Seed(conversation);
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        var tags = new FakeTagRepository();
        var operatorTag = Tag.Create(new TagId(Guid.NewGuid()), SiteId, "Billing", Now);
        var aiTag = Tag.Create(new TagId(Guid.NewGuid()), SiteId, "Shipping", Now);
        tags.Seed(operatorTag);
        tags.Seed(aiTag);
        tags.SeedAssociation(conversation.Id, operatorTag.Id, TagSource.Operator);
        tags.SeedAssociation(conversation.Id, aiTag.Id, TagSource.Ai);
        var handler = new GetConversationTagsHandler(readStore, tags, permissions);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetConversationTags.GetConversationTags(conversation.Id, SiteId, OperatorId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Operator", result.Value.Single(t => t.Name == "Billing").Source);
        Assert.Equal("Ai", result.Value.Single(t => t.Name == "Shipping").Source);
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
