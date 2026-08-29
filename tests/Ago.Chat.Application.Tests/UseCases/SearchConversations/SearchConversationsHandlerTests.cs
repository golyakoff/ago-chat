using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.SearchConversations;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.SearchConversations;

public class SearchConversationsHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly SiteId OtherSiteId = new(Guid.NewGuid());
    private static readonly OperatorId AdminId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static (SearchConversationsHandler Handler, FakeConversationSearchStore Store, FakePermissionChecker Permissions) CreateFixture(
        bool grantPermission = true)
    {
        var store = new FakeConversationSearchStore();
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(AdminId, SiteId, Permission.SiteConfigure);
        }

        var clock = new FakeClock(Now);
        return (new SearchConversationsHandler(store, permissions, clock), store, permissions);
    }

    [Fact]
    public async Task HandleAsync_WithoutSiteConfigure_ReturnsForbidden()
    {
        var (handler, _, _) = CreateFixture(grantPermission: false);

        var result = await handler.HandleAsync(
            new Application.UseCases.SearchConversations.SearchConversations(AdminId, SiteId, "refund", null, null, null, 20), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WithAnEmptyPhrase_ReturnsSearchInvalidQuery()
    {
        var (handler, _, _) = CreateFixture();

        var result = await handler.HandleAsync(
            new Application.UseCases.SearchConversations.SearchConversations(AdminId, SiteId, "   ", null, null, null, 20), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.SearchInvalidQuery", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenFromIsNotBeforeTo_ReturnsSearchInvalidQuery()
    {
        var (handler, _, _) = CreateFixture();

        var result = await handler.HandleAsync(
            new Application.UseCases.SearchConversations.SearchConversations(AdminId, SiteId, "refund", Now, Now.AddDays(-1), null, 20), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.SearchInvalidQuery", result.Error!.Value.Code);
    }

    /// <summary>`18-01`'s own bound decision: naming no range does not reject the search, it defaults
    /// one - and the response always echoes back exactly what was searched
    /// (`SearchConversationsHandler.DefaultWindowMonths`), so the console can show it rather than the
    /// operator having to infer a silent truncation.</summary>
    [Fact]
    public async Task HandleAsync_WhenNoRangeIsSupplied_DefaultsToTheTrailingWindow_AndEchoesItBack()
    {
        var (handler, store, _) = CreateFixture();

        var result = await handler.HandleAsync(
            new Application.UseCases.SearchConversations.SearchConversations(AdminId, SiteId, "refund", null, null, null, 20), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var expectedFrom = Now.AddMonths(-SearchConversationsHandler.DefaultWindowMonths);
        Assert.Equal(expectedFrom, result.Value.SearchedFrom);
        Assert.Equal(Now, result.Value.SearchedTo);
        Assert.Equal(expectedFrom, store.LastFrom);
        Assert.Equal(Now, store.LastTo);
    }

    [Fact]
    public async Task HandleAsync_WhenARangeIsSupplied_PassesItThroughUnchanged()
    {
        var (handler, store, _) = CreateFixture();
        var from = Now.AddDays(-10);
        var to = Now.AddDays(-1);

        var result = await handler.HandleAsync(
            new Application.UseCases.SearchConversations.SearchConversations(AdminId, SiteId, "refund", from, to, null, 20), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(from, result.Value.SearchedFrom);
        Assert.Equal(to, result.Value.SearchedTo);
        Assert.Equal(from, store.LastFrom);
        Assert.Equal(to, store.LastTo);
    }

    [Fact]
    public async Task HandleAsync_PassesTheCallersOwnSiteId_NeverAnother()
    {
        var (handler, store, _) = CreateFixture();

        await handler.HandleAsync(
            new Application.UseCases.SearchConversations.SearchConversations(AdminId, SiteId, "refund", null, null, null, 20), CancellationToken.None);

        Assert.Equal(SiteId, store.LastSiteId);
        Assert.NotEqual(OtherSiteId, store.LastSiteId);
    }

    [Fact]
    public async Task HandleAsync_MapsResultsFromTheStore()
    {
        var (handler, store, _) = CreateFixture();
        var conversationId = new ConversationId(Guid.NewGuid());
        var messageId = new MessageId(Guid.NewGuid());
        store.Seed(new ConversationSearchResultItem(
            conversationId, messageId, 3, "please refund my order", MessageAuthorKind.Visitor, Now.AddDays(-1), "Waiting"));

        var result = await handler.HandleAsync(
            new Application.UseCases.SearchConversations.SearchConversations(AdminId, SiteId, "refund", null, null, null, 20), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var hit = Assert.Single(result.Value.Results);
        Assert.Equal(conversationId.Value, hit.ConversationId);
        Assert.Equal(messageId.Value, hit.MessageId);
        Assert.Equal(3, hit.Sequence);
        Assert.Equal("please refund my order", hit.MatchedBody);
        Assert.Equal("Waiting", hit.ConversationState);
    }
}
