using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.RenameTag;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RenameTag;

public class RenameTagHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(RenameTagHandler Handler, FakeTagRepository Tags, TagId TagId);

    private static Fixture CreateFixture(bool grantPermission = true)
    {
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        }

        var tags = new FakeTagRepository();
        var tag = Tag.Create(new TagId(Guid.NewGuid()), SiteId, "VIP", Now);
        tags.Seed(tag);

        return new Fixture(new RenameTagHandler(tags, permissions), tags, tag.Id);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_RenamesTheTag()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RenameTag.RenameTag(SiteId, fixture.TagId, OperatorId, "Priority"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Priority", result.Value.Name);
    }

    [Fact]
    public async Task HandleAsync_UnknownTag_ReturnsNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RenameTag.RenameTag(SiteId, new TagId(Guid.NewGuid()), OperatorId, "Priority"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Tag.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_RenamingToAnotherTagsName_ReturnsAlreadyExists()
    {
        var fixture = CreateFixture();
        fixture.Tags.Seed(Tag.Create(new TagId(Guid.NewGuid()), SiteId, "Priority", Now));

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RenameTag.RenameTag(SiteId, fixture.TagId, OperatorId, "priority"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Tag.AlreadyExists", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_RenamingToItsOwnCurrentName_Succeeds()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RenameTag.RenameTag(SiteId, fixture.TagId, OperatorId, "VIP"), CancellationToken.None);

        Assert.True(result.IsSuccess);
    }
}
