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
        // `25-221`: deliberately left Pending (never messaged) - this handler's own point is "every
        // conversation for the site, regardless", and that now includes one nothing has routed yet.
        var pending = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, new VisitorId(Guid.NewGuid()), Now);
        var assignedToSomeoneElse = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, new VisitorId(Guid.NewGuid()), Now);
        assignedToSomeoneElse.AddVisitorMessage(assignedToSomeoneElse.VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        assignedToSomeoneElse.AssignTo(OtherOperatorId, Now);
        readStore.Seed(pending);
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

    // `25-56`'s own second half: the read store's `VisitorName` rides through to the wire DTO
    // unchanged, and a conversation whose visitor never gave one carries a null rather than an empty
    // string or a placeholder.
    [Fact]
    public async Task HandleAsync_AVisitorWithAName_CarriesItOnTheSummary_AndOneWithoutOneCarriesNull()
    {
        var (handler, readStore) = CreateFixture();
        var named = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, new VisitorId(Guid.NewGuid()), Now);
        var unnamed = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, new VisitorId(Guid.NewGuid()), Now);
        readStore.Seed(named);
        readStore.Seed(unnamed);
        readStore.SeedVisitorName(named.VisitorId, "Иван Иванов");

        var result = await handler.HandleAsync(new Application.UseCases.GetAllConversationsForSite.GetAllConversationsForSite(AdminId, SiteId, null, 50), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Иван Иванов", result.Value.Conversations.Single(c => c.ConversationId == named.Id.Value).VisitorName);
        Assert.Null(result.Value.Conversations.Single(c => c.ConversationId == unnamed.Id.Value).VisitorName);
    }

    // `26-90`: the fields Android's "Все" tab renders on its second and third lines. Both were absent
    // from this list before this item - the DTO carried them, this handler never filled them - so this
    // test is the one that would have failed against the old code.
    [Fact]
    public async Task HandleAsync_CarriesTheLastMessagePreviewItsTimestampAndTheTotalMessageCount()
    {
        var (handler, readStore) = CreateFixture();
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, visitorId, Now);
        conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("первое"), Now);
        conversation.AddVisitorMessage(
            visitorId, new MessageId(Guid.NewGuid()), new MessageBody("оплата не прошла"), Now.AddMinutes(3));
        readStore.Seed(conversation);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetAllConversationsForSite.GetAllConversationsForSite(AdminId, SiteId, null, 50),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var row = Assert.Single(result.Value.Conversations);
        Assert.Equal("оплата не прошла", row.LastMessagePreview);
        Assert.Equal(Now.AddMinutes(3), row.LastMessageAt);
        // A total, never an unread count - OperatorUnreadCount keeps its own separate meaning, which is
        // why both are asserted here rather than only the new one.
        Assert.Equal(2, row.MessageCount);
    }

    // The empty-conversation edge: `25-221`'s Pending row has no messages at all, and must read as
    // "nothing said yet" rather than as a zero-length message or a placeholder string.
    [Fact]
    public async Task HandleAsync_AConversationWithNoMessages_HasNoPreviewNoTimestampAndACountOfZero()
    {
        var (handler, readStore) = CreateFixture();
        readStore.Seed(Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, new VisitorId(Guid.NewGuid()), Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.GetAllConversationsForSite.GetAllConversationsForSite(AdminId, SiteId, null, 50),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var row = Assert.Single(result.Value.Conversations);
        Assert.Null(row.LastMessagePreview);
        Assert.Null(row.LastMessageAt);
        Assert.Equal(0, row.MessageCount);
    }

    // `26-90`: the tab's own default filter - Не начат + Назначен on, Закрыт off. The assertion that
    // matters is that the *closed* one is absent: the whole reason this is a query parameter and not a
    // client-side `.filter()` is that this list is keyset-paginated.
    [Fact]
    public async Task HandleAsync_WithAStateFilter_ReturnsOnlyConversationsInThoseStates()
    {
        var (handler, readStore) = CreateFixture();
        var waiting = Seed(readStore, assignTo: null, close: false);
        var assigned = Seed(readStore, assignTo: OtherOperatorId, close: false);
        var closed = Seed(readStore, assignTo: OtherOperatorId, close: true);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetAllConversationsForSite.GetAllConversationsForSite(
                AdminId, SiteId, null, 50, Tag: null,
                States: [nameof(ConversationState.Waiting), nameof(ConversationState.Assigned)]),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Conversations.Count);
        Assert.Contains(result.Value.Conversations, c => c.ConversationId == waiting.Id.Value);
        Assert.Contains(result.Value.Conversations, c => c.ConversationId == assigned.Id.Value);
        Assert.DoesNotContain(result.Value.Conversations, c => c.ConversationId == closed.Id.Value);
    }

    // An empty list is not a filter that matches nothing - it is the absence of a filter, the same rule
    // `AllForSiteSql`'s own `cardinality(@States) = 0` branch implements.
    [Fact]
    public async Task HandleAsync_WithAnEmptyStateFilter_IsUnfiltered()
    {
        var (handler, readStore) = CreateFixture();
        Seed(readStore, assignTo: null, close: false);
        Seed(readStore, assignTo: OtherOperatorId, close: true);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetAllConversationsForSite.GetAllConversationsForSite(
                AdminId, SiteId, null, 50, Tag: null, States: []),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Conversations.Count);
    }

    // Refused, not silently dropped - a dropped typo would answer "every state" while looking exactly
    // like a filter that worked (this handler's own remarks).
    [Fact]
    public async Task HandleAsync_WithAStateThatIsNotAConversationState_ReturnsInvalidState()
    {
        var (handler, readStore) = CreateFixture();
        Seed(readStore, assignTo: null, close: false);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetAllConversationsForSite.GetAllConversationsForSite(
                AdminId, SiteId, null, 50, Tag: null, States: [nameof(ConversationState.Waiting), "Archived"]),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.InvalidState", result.Error!.Value.Code);
        Assert.Contains("Archived", result.Error!.Value.Message);
    }

    // The permission check runs before the states are even parsed - a caller with no standing on this
    // site must not learn whether the values they sent were valid (GetSiteConsentAcceptancesHandler's
    // own precedent for the identical ordering).
    [Fact]
    public async Task HandleAsync_WithoutSiteConfigureAndAnInvalidState_ReturnsForbiddenRatherThanInvalidState()
    {
        var (handler, _) = CreateFixture(grantPermission: false);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetAllConversationsForSite.GetAllConversationsForSite(
                AdminId, SiteId, null, 50, Tag: null, States: ["Archived"]),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    private static Conversation Seed(FakeConversationReadStore readStore, OperatorId? assignTo, bool close)
    {
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, visitorId, Now);
        conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        if (assignTo is { } operatorId)
        {
            conversation.AssignTo(operatorId, Now);
        }

        if (close)
        {
            conversation.Close(Now);
        }

        readStore.Seed(conversation);
        return conversation;
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
