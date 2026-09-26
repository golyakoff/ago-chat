using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetConversationHistoryAsSiteConfigureHolder;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetConversationHistoryAsSiteConfigureHolder;

public class GetConversationHistoryAsSiteConfigureHolderHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId AssignedOperatorId = new(Guid.NewGuid());
    private static readonly OperatorId SiteConfigureHolderId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static (
        GetConversationHistoryAsSiteConfigureHolderHandler Handler,
        FakePermissionChecker Permissions,
        FakeConversationRepository Conversations,
        Conversation Conversation)
        CreateHandlerWithHistory()
    {
        var conversations = new FakeConversationRepository();
        var readStore = new FakeConversationReadStore();
        var permissions = new FakePermissionChecker();

        // `26-98`'s whole point: a conversation assigned to someone else entirely, never to the caller
        // reading it below - the exact shape `AllRow`'s own tap sends the server today.
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        conversation.AssignTo(AssignedOperatorId, Now);
        conversation.AddOperatorMessage(AssignedOperatorId, new MessageId(Guid.NewGuid()), new MessageBody("hello"), Now);

        conversations.Seed(conversation);
        readStore.Seed(conversation);
        permissions.Grant(SiteConfigureHolderId, SiteId, Permission.SiteConfigure);

        var handler = new GetConversationHistoryAsSiteConfigureHolderHandler(conversations, readStore, permissions);
        return (handler, permissions, conversations, conversation);
    }

    [Fact]
    public async Task HandleAsync_WhenPermittedAndSameSite_ReturnsTheHistory_WithNoAssignmentRequired()
    {
        var (handler, _, _, conversation) = CreateHandlerWithHistory();

        var result = await handler.HandleAsync(
            new GetConversationHistoryAsSiteConfigureHolderQuery(
                conversation.Id, SiteConfigureHolderId, SiteId, BeforeSequence: null, PageSize: 10),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Messages.Count);
        // The caller above is never the operator `AssignTo` put on this conversation - this handler
        // never checks for that at all, which is the entire reason `26-98` needed it.
        Assert.NotEqual(AssignedOperatorId, SiteConfigureHolderId);
    }

    [Fact]
    public async Task HandleAsync_NeverAssignsTheConversationToTheCaller()
    {
        var (handler, _, conversations, conversation) = CreateHandlerWithHistory();

        await handler.HandleAsync(
            new GetConversationHistoryAsSiteConfigureHolderQuery(
                conversation.Id, SiteConfigureHolderId, SiteId, BeforeSequence: null, PageSize: 10),
            CancellationToken.None);

        var reloaded = await conversations.GetByIdAsync(conversation.Id, CancellationToken.None);
        Assert.Equal(AssignedOperatorId, reloaded!.OperatorId);
    }

    [Fact]
    public async Task HandleAsync_WhenNotPermitted_ReturnsForbidden()
    {
        var (handler, _, _, conversation) = CreateHandlerWithHistory();
        var unpermitted = new OperatorId(Guid.NewGuid());

        var result = await handler.HandleAsync(
            new GetConversationHistoryAsSiteConfigureHolderQuery(
                conversation.Id, unpermitted, SiteId, BeforeSequence: null, PageSize: 10),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenConversationDoesNotExist_ReturnsNotFound()
    {
        var conversations = new FakeConversationRepository();
        var readStore = new FakeConversationReadStore();
        var permissions = new FakePermissionChecker();
        permissions.Grant(SiteConfigureHolderId, SiteId, Permission.SiteConfigure);
        var handler = new GetConversationHistoryAsSiteConfigureHolderHandler(conversations, readStore, permissions);

        var result = await handler.HandleAsync(
            new GetConversationHistoryAsSiteConfigureHolderQuery(
                new ConversationId(Guid.NewGuid()), SiteConfigureHolderId, SiteId, BeforeSequence: null, PageSize: 10),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    // The check this handler's own remarks call load-bearing: with no assignment to lean on, this is
    // the only thing standing between "reads this tenant's own conversations" and "reads any tenant's
    // conversation by id" - `Conversation.NotFound`, not `Forbidden`, matching the "wrong tenant reads
    // like no row" info-hiding shape every cross-tenant guard in this codebase already uses.
    [Fact]
    public async Task HandleAsync_WhenTheConversationBelongsToADifferentSite_ReturnsNotFound()
    {
        var (handler, permissions, _, conversation) = CreateHandlerWithHistory();
        var otherSite = new SiteId(Guid.NewGuid());
        permissions.Grant(SiteConfigureHolderId, otherSite, Permission.SiteConfigure);

        var result = await handler.HandleAsync(
            new GetConversationHistoryAsSiteConfigureHolderQuery(
                conversation.Id, SiteConfigureHolderId, otherSite, BeforeSequence: null, PageSize: 10),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    // `24-10`: unreachable, not merely hidden - the same rule every other operator-facing read in this
    // codebase already gives a blocked conversation.
    [Fact]
    public async Task HandleAsync_WhenTheConversationIsBlocked_ReturnsNotFound()
    {
        var (handler, _, _, conversation) = CreateHandlerWithHistory();
        conversation.MarkBlockedForTesting(new OperatorId(Guid.NewGuid()), Now);

        var result = await handler.HandleAsync(
            new GetConversationHistoryAsSiteConfigureHolderQuery(
                conversation.Id, SiteConfigureHolderId, SiteId, BeforeSequence: null, PageSize: 10),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }
}
