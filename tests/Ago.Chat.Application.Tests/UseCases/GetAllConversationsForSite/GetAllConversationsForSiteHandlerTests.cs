using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetAllConversationsForSite;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetAllConversationsForSite;

public class GetAllConversationsForSiteHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId AdminId = new(Guid.NewGuid());
    private static readonly OperatorId OtherOperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static (GetAllConversationsForSiteHandler Handler, FakeConversationReadStore ReadStore) CreateFixture(bool grantPermission = true)
    {
        var readStore = new FakeConversationReadStore();
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(AdminId, SiteId, Permission.SiteConfigure);
        }

        return (new GetAllConversationsForSiteHandler(readStore, permissions), readStore);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorHoldsSiteConfigure_ReturnsEveryConversationForTheSite_RegardlessOfAssignment()
    {
        var (handler, readStore) = CreateFixture();
        var waiting = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, new VisitorId(Guid.NewGuid()), Now);
        var assignedToSomeoneElse = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, new VisitorId(Guid.NewGuid()), Now);
        assignedToSomeoneElse.AssignTo(OtherOperatorId, Now);
        readStore.Seed(waiting);
        readStore.Seed(assignedToSomeoneElse);

        var result = await handler.HandleAsync(new Application.UseCases.GetAllConversationsForSite.GetAllConversationsForSite(AdminId, SiteId, null, 50), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Conversations.Count);
        Assert.Contains(result.Value.Conversations, c => c.ConversationId == assignedToSomeoneElse.Id.Value && c.OperatorId == OtherOperatorId.Value);
    }

    [Fact]
    public async Task HandleAsync_WithATagFilter_ReturnsOnlyConversationsCarryingThatTag()
    {
        var (handler, readStore) = CreateFixture();
        var tagged = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, new VisitorId(Guid.NewGuid()), Now);
        var untagged = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, new VisitorId(Guid.NewGuid()), Now);
        readStore.Seed(tagged);
        readStore.Seed(untagged);
        var tagId = new TagId(Guid.NewGuid());
        readStore.SeedTag(tagged.Id, tagId);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetAllConversationsForSite.GetAllConversationsForSite(AdminId, SiteId, null, 50, tagId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(tagged.Id.Value, Assert.Single(result.Value.Conversations).ConversationId);
    }

    [Fact]
    public async Task HandleAsync_WithoutSiteConfigure_ReturnsForbidden()
    {
        var (handler, _) = CreateFixture(grantPermission: false);

        var result = await handler.HandleAsync(new Application.UseCases.GetAllConversationsForSite.GetAllConversationsForSite(AdminId, SiteId, null, 50), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }
}
