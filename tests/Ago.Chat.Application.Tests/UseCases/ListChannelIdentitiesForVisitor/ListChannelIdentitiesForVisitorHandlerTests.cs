using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ListChannelIdentitiesForVisitor;

public class ListChannelIdentitiesForVisitorHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly ConversationId ConversationId = new(Guid.NewGuid());

    private sealed record Fixture(
        Application.UseCases.ListChannelIdentitiesForVisitor.ListChannelIdentitiesForVisitorHandler Handler,
        FakeChannelIdentityRepository Identities);

    private static Fixture CreateFixture(bool permitted = true, bool assigned = true)
    {
        var conversation = Conversation.Start(ConversationId, SiteId, VisitorId, Now);
        if (assigned)
        {
            conversation.AssignTo(OperatorId, Now);
        }

        var conversations = new FakeConversationRepository();
        conversations.Seed(conversation);

        var identities = new FakeChannelIdentityRepository();
        var permissions = new FakePermissionChecker();
        if (permitted)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        }

        var handler = new Application.UseCases.ListChannelIdentitiesForVisitor.ListChannelIdentitiesForVisitorHandler(
            conversations, identities, permissions);
        return new Fixture(handler, identities);
    }

    private static Application.UseCases.ListChannelIdentitiesForVisitor.ListChannelIdentitiesForVisitor Query() =>
        new(ConversationId, OperatorId, SiteId);

    [Fact]
    public async Task HandleAsync_ListsOnlyTheVisitorsActiveIdentities()
    {
        var fixture = CreateFixture();
        var active = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), SiteId, ChannelKind.Telegram,
            new ExternalChannelAddress("tg-user-1"), VisitorId, Now);
        await fixture.Identities.SaveAsync(active, CancellationToken.None);
        var unlinked = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), SiteId, ChannelKind.Vk,
            new ExternalChannelAddress("vk-user-1"), VisitorId, Now);
        unlinked.Unlink(Now.AddHours(1));
        await fixture.Identities.SaveAsync(unlinked, CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(Query(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var summary = Assert.Single(result.Value);
        Assert.Equal(active.Id.Value, summary.ChannelIdentityId);
        Assert.Equal(ChannelKind.Telegram, summary.Kind);
    }

    [Fact]
    public async Task HandleAsync_WithoutPermission_ReturnsForbidden()
    {
        var fixture = CreateFixture(permitted: false);

        var result = await fixture.Handler.HandleAsync(Query(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_AnOperatorNotAssignedToTheConversation_ReturnsForbidden()
    {
        var fixture = CreateFixture(assigned: false);

        var result = await fixture.Handler.HandleAsync(Query(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_ANonExistentConversation_ReturnsNotFound()
    {
        var fixture = CreateFixture();
        var query = new Application.UseCases.ListChannelIdentitiesForVisitor.ListChannelIdentitiesForVisitor(
            new ConversationId(Guid.NewGuid()), OperatorId, SiteId);

        var result = await fixture.Handler.HandleAsync(query, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }
}
