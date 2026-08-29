using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.CreateTag;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.CreateTag;

public class CreateTagHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(CreateTagHandler Handler, FakeTagRepository Tags);

    private static Fixture CreateFixture(bool grantPermission = true)
    {
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        }

        var tags = new FakeTagRepository();
        return new Fixture(new CreateTagHandler(tags, permissions, new FakeIdGenerator(), new FakeClock(Now)), tags);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_CreatesTheTag()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.CreateTag.CreateTag(SiteId, OperatorId, "VIP"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("VIP", result.Value.Name);
        var saved = Assert.Single(await fixture.Tags.GetAllForSiteAsync(SiteId, CancellationToken.None));
        Assert.Equal("VIP", saved.Name);
    }

    [Fact]
    public async Task HandleAsync_OperatorWithoutPermission_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.CreateTag.CreateTag(SiteId, OperatorId, "VIP"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_DuplicateName_ReturnsAlreadyExists()
    {
        var fixture = CreateFixture();
        fixture.Tags.Seed(Tag.Create(new TagId(Guid.NewGuid()), SiteId, "VIP", Now));

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.CreateTag.CreateTag(SiteId, OperatorId, "vip"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Tag.AlreadyExists", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_EmptyName_ReturnsTagInvalid()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.CreateTag.CreateTag(SiteId, OperatorId, "   "), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Tag.Invalid", result.Error!.Value.Code);
    }
}
