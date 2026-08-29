using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.DeleteTag;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.DeleteTag;

public class DeleteTagHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_WhenPermitted_DeletesTheTag()
    {
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        var tags = new FakeTagRepository();
        var tag = Tag.Create(new TagId(Guid.NewGuid()), SiteId, "VIP", Now);
        tags.Seed(tag);
        var handler = new DeleteTagHandler(tags, permissions);

        var result = await handler.HandleAsync(
            new Application.UseCases.DeleteTag.DeleteTag(SiteId, tag.Id, OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(await tags.GetAllForSiteAsync(SiteId, CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_OperatorWithoutPermission_ReturnsForbidden()
    {
        var permissions = new FakePermissionChecker();
        var tags = new FakeTagRepository();
        var tag = Tag.Create(new TagId(Guid.NewGuid()), SiteId, "VIP", Now);
        tags.Seed(tag);
        var handler = new DeleteTagHandler(tags, permissions);

        var result = await handler.HandleAsync(
            new Application.UseCases.DeleteTag.DeleteTag(SiteId, tag.Id, OperatorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_UnknownTag_ReturnsNotFound()
    {
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        var handler = new DeleteTagHandler(new FakeTagRepository(), permissions);

        var result = await handler.HandleAsync(
            new Application.UseCases.DeleteTag.DeleteTag(SiteId, new TagId(Guid.NewGuid()), OperatorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Tag.NotFound", result.Error!.Value.Code);
    }
}
