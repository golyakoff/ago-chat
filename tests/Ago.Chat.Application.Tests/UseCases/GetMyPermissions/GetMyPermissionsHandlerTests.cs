using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetMyPermissions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetMyPermissions;

public class GetMyPermissionsHandlerTests
{
    [Fact]
    public async Task HandleAsync_ReturnsEveryPermissionTheOperatorsRolesGrantForThatSite()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var permissions = new FakePermissionChecker();
        permissions.Grant(operatorId, siteId, Permission.ConversationRead);
        permissions.Grant(operatorId, siteId, Permission.AttachmentDelete);
        var handler = new GetMyPermissionsHandler(permissions);

        var result = await handler.HandleAsync(new Application.UseCases.GetMyPermissions.GetMyPermissions(operatorId, siteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(operatorId.Value, result.Value.OperatorId);
        Assert.Equal(siteId.Value, result.Value.SiteId);
        Assert.Contains(Permission.ConversationRead.Value, result.Value.Permissions);
        Assert.Contains(Permission.AttachmentDelete.Value, result.Value.Permissions);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorHasNoRoleForThisSite_ReturnsAnEmptyList()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var handler = new GetMyPermissionsHandler(new FakePermissionChecker());

        var result = await handler.HandleAsync(new Application.UseCases.GetMyPermissions.GetMyPermissions(operatorId, siteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Permissions);
    }
}
