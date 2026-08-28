using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetVisitorHistory;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetVisitorHistory;

public class GetVisitorHistoryHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId AssignedOperatorId = new(Guid.NewGuid());
    private static readonly OperatorId OtherOperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        GetVisitorHistoryHandler Handler,
        FakeConversationRepository Conversations,
        FakeConversationReadStore ReadStore,
        FakeChannelIdentityRepository ChannelIdentities,
        FakePermissionChecker Permissions,
        Conversation CurrentConversation);

    private static Fixture CreateFixture(bool grantConversationRead = true)
    {
        var conversations = new FakeConversationRepository();
        var readStore = new FakeConversationReadStore();
        var channelIdentities = new FakeChannelIdentityRepository();
        var permissions = new FakePermissionChecker();
        if (grantConversationRead)
        {
            permissions.Grant(AssignedOperatorId, SiteId, Permission.ConversationRead);
            permissions.Grant(OtherOperatorId, SiteId, Permission.ConversationRead);
        }

        var current = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        current.AssignTo(AssignedOperatorId, Now);
        conversations.Seed(current);
        readStore.Seed(current);

        var handler = new GetVisitorHistoryHandler(conversations, readStore, channelIdentities, permissions);
        return new Fixture(handler, conversations, readStore, channelIdentities, permissions, current);
    }

    private static void LinkChannelIdentity(FakeChannelIdentityRepository channelIdentities, DateTimeOffset lastSeenAt)
    {
        var identity = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), SiteId, ChannelKind.Sms,
            new ExternalChannelAddress("+15551234567"), VisitorId, lastSeenAt);
        channelIdentities.SaveAsync(identity, CancellationToken.None).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task HandleAsOperatorAsync_ForAChannelIdentifiedVisitor_ReturnsPriorConversations_MostRecentFirst_ExcludingTheCurrentOne()
    {
        var fixture = CreateFixture();
        LinkChannelIdentity(fixture.ChannelIdentities, Now);

        var older = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now.AddDays(-2));
        older.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("older visit"), Now.AddDays(-2));
        older.Close(Now.AddDays(-2).AddHours(1));

        var newer = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now.AddDays(-1));
        newer.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("newer visit"), Now.AddDays(-1));
        newer.Close(Now.AddDays(-1).AddHours(1));

        fixture.Conversations.Seed(older);
        fixture.Conversations.Seed(newer);
        fixture.ReadStore.Seed(older);
        fixture.ReadStore.Seed(newer);

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new Application.UseCases.GetVisitorHistory.GetVisitorHistory(
                fixture.CurrentConversation.Id, AssignedOperatorId, SiteId, null, 50),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.HasChannelIdentity);
        Assert.DoesNotContain(result.Value.Conversations, c => c.ConversationId == fixture.CurrentConversation.Id.Value);
        // The fake's own "most recent first" ordering sorts by raw conversation-id byte order, the
        // same cursor production's real uuid v7 ids give for free (IIdGenerator's own remarks) - the
        // test double's ids come from a plain Guid.NewGuid() (FakeIdGenerator, deliberately not
        // time-ordered), so asserting a specific order here would be testing the fake's random id
        // generator, not this handler. The real "newest first" guarantee over real, time-ordered ids
        // is ConversationReadStoreTests.GetVisitorHistoryAsync_ReturnsConversationsNewestFirst's job,
        // against a real Postgres. Here: both priors came back, and only both.
        Assert.Equal(2, result.Value.Conversations.Count);
        Assert.Contains(result.Value.Conversations, c => c.ConversationId == newer.Id.Value);
        Assert.Contains(result.Value.Conversations, c => c.ConversationId == older.Id.Value);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_ForAWidgetVisitorWithNoChannelIdentity_ReturnsHasChannelIdentityFalse_AndAnEmptyList_WithoutQueryingHistory()
    {
        var fixture = CreateFixture();
        // No LinkChannelIdentity call - this visitor has never been heard from on any channel, the
        // ordinary shape for a widget-only visitor (14-01's model).

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new Application.UseCases.GetVisitorHistory.GetVisitorHistory(
                fixture.CurrentConversation.Id, AssignedOperatorId, SiteId, null, 50),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.HasChannelIdentity);
        Assert.Empty(result.Value.Conversations);
        Assert.Null(result.Value.NextBeforeId);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_WhenTheOperatorIsNotAssignedToTheConversation_ReturnsForbidden_EvenThoughTheyHoldConversationReadAtTheSameSite()
    {
        var fixture = CreateFixture();
        LinkChannelIdentity(fixture.ChannelIdentities, Now);

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new Application.UseCases.GetVisitorHistory.GetVisitorHistory(
                fixture.CurrentConversation.Id, OtherOperatorId, SiteId, null, 50),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_WithoutConversationReadPermission_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantConversationRead: false);

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new Application.UseCases.GetVisitorHistory.GetVisitorHistory(
                fixture.CurrentConversation.Id, AssignedOperatorId, SiteId, null, 50),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_ForAnUnknownConversation_ReturnsNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new Application.UseCases.GetVisitorHistory.GetVisitorHistory(
                new ConversationId(Guid.NewGuid()), AssignedOperatorId, SiteId, null, 50),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleHistoricalConversationAsOperatorAsync_ForAPastConversationOfTheSameVisitor_ReturnsItsRealHistory_EvenThoughADifferentOperatorOriginallyHeldIt()
    {
        var fixture = CreateFixture();
        var historical = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now.AddDays(-1));
        historical.AssignTo(OtherOperatorId, Now.AddDays(-1));
        historical.AddOperatorMessage(OtherOperatorId, new MessageId(Guid.NewGuid()), new MessageBody("handled by someone else"), Now.AddDays(-1));
        historical.Close(Now.AddDays(-1).AddHours(1));
        fixture.Conversations.Seed(historical);
        fixture.ReadStore.Seed(historical);

        var result = await fixture.Handler.HandleHistoricalConversationAsOperatorAsync(
            new Application.UseCases.GetVisitorHistory.GetVisitorHistoryConversation(
                fixture.CurrentConversation.Id, historical.Id, AssignedOperatorId, SiteId, null, 50),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var message = Assert.Single(result.Value.Messages);
        Assert.Equal("handled by someone else", message.Body);
    }

    [Fact]
    public async Task HandleHistoricalConversationAsOperatorAsync_ForAConversationOfADifferentVisitor_ReturnsForbidden()
    {
        var fixture = CreateFixture();
        var otherVisitorConversation = Conversation.Start(
            new ConversationId(Guid.NewGuid()), SiteId, new VisitorId(Guid.NewGuid()), Now.AddDays(-1));
        otherVisitorConversation.AddVisitorMessage(
            otherVisitorConversation.VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("not this visitor"), Now.AddDays(-1));
        fixture.Conversations.Seed(otherVisitorConversation);
        fixture.ReadStore.Seed(otherVisitorConversation);

        var result = await fixture.Handler.HandleHistoricalConversationAsOperatorAsync(
            new Application.UseCases.GetVisitorHistory.GetVisitorHistoryConversation(
                fixture.CurrentConversation.Id, otherVisitorConversation.Id, AssignedOperatorId, SiteId, null, 50),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleHistoricalConversationAsOperatorAsync_WhenTheCallerIsNotAssignedToTheirOwnStandingConversation_ReturnsForbidden()
    {
        var fixture = CreateFixture();
        var historical = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now.AddDays(-1));
        historical.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("past message"), Now.AddDays(-1));
        fixture.Conversations.Seed(historical);
        fixture.ReadStore.Seed(historical);

        var result = await fixture.Handler.HandleHistoricalConversationAsOperatorAsync(
            new Application.UseCases.GetVisitorHistory.GetVisitorHistoryConversation(
                fixture.CurrentConversation.Id, historical.Id, OtherOperatorId, SiteId, null, 50),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleHistoricalConversationAsOperatorAsync_ForAnUnknownHistoricalConversation_ReturnsNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleHistoricalConversationAsOperatorAsync(
            new Application.UseCases.GetVisitorHistory.GetVisitorHistoryConversation(
                fixture.CurrentConversation.Id, new ConversationId(Guid.NewGuid()), AssignedOperatorId, SiteId, null, 50),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }
}
