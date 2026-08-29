using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.ListTags;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ListTags;

public class ListTagsHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_WhenPermitted_ReturnsEveryTagForTheSite()
    {
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        var tags = new FakeTagRepository();
        tags.Seed(Tag.Create(new TagId(Guid.NewGuid()), SiteId, "VIP", Now));
        tags.Seed(Tag.Create(new TagId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), "OtherSite", Now));
        var handler = new ListTagsHandler(tags, permissions);

        var result = await handler.HandleAsync(new Application.UseCases.ListTags.ListTags(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(["VIP"], result.Value.Select(t => t.Name));
    }

    [Fact]
    public async Task HandleAsync_OperatorWithoutPermission_ReturnsForbidden()
    {
        var handler = new ListTagsHandler(new FakeTagRepository(), new FakePermissionChecker());

        var result = await handler.HandleAsync(new Application.UseCases.ListTags.ListTags(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }
}
