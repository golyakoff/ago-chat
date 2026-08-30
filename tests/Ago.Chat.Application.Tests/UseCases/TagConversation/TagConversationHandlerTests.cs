using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.TagConversation;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.TagConversation;

public class TagConversationHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(TagConversationHandler Handler, FakeTagRepository Tags, ConversationId ConversationId, TagId TagId);

    private static Fixture CreateFixture(bool grantPermission = true)
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        var readStore = new FakeConversationReadStore();
        readStore.Seed(conversation);

        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationTag);
        }

        var tags = new FakeTagRepository();
        var tag = Tag.Create(new TagId(Guid.NewGuid()), SiteId, "VIP", Now);
        tags.Seed(tag);

        return new Fixture(new TagConversationHandler(readStore, tags, permissions), tags, conversation.Id, tag.Id);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_AppliesTheTag()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.TagConversation.TagConversation(fixture.ConversationId, SiteId, fixture.TagId, OperatorId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var applied = Assert.Single(await fixture.Tags.GetForConversationAsync(fixture.ConversationId, CancellationToken.None));
        Assert.Equal(fixture.TagId, applied.Tag.Id);
        // `19-02`: an operator's own action always writes TagSource.Operator, never Ai -
        // TagConversationHandler's own remarks on why this is never defaulted.
        Assert.Equal(TagSource.Operator, applied.Source);
    }

    [Fact]
    public async Task HandleAsync_AppliedTwice_IsIdempotent()
    {
        var fixture = CreateFixture();
        var command = new Application.UseCases.TagConversation.TagConversation(fixture.ConversationId, SiteId, fixture.TagId, OperatorId);

        await fixture.Handler.HandleAsync(command, CancellationToken.None);
        var result = await fixture.Handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(await fixture.Tags.GetForConversationAsync(fixture.ConversationId, CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_OperatorWithoutPermission_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.TagConversation.TagConversation(fixture.ConversationId, SiteId, fixture.TagId, OperatorId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_UnknownConversation_ReturnsNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.TagConversation.TagConversation(new ConversationId(Guid.NewGuid()), SiteId, fixture.TagId, OperatorId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_UnknownTag_ReturnsTagNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.TagConversation.TagConversation(fixture.ConversationId, SiteId, new TagId(Guid.NewGuid()), OperatorId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Tag.NotFound", result.Error!.Value.Code);
    }
}
