using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetOperatorQueue;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetOperatorQueue;

public class GetOperatorQueueHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_ReturnsWaitingConversationsForTheSite_AndAssignedConversationsForThisOperator()
    {
        var waiting = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        var assignedToMe = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, new VisitorId(Guid.NewGuid()), Now);
        assignedToMe.AssignTo(OperatorId, Now);
        var assignedToSomeoneElse = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, new VisitorId(Guid.NewGuid()), Now);
        assignedToSomeoneElse.AssignTo(new OperatorId(Guid.NewGuid()), Now);

        var (handler, conversations) = CreateHandler();
        conversations.Seed(waiting);
        conversations.Seed(assignedToMe);
        conversations.Seed(assignedToSomeoneElse);

        var result = await handler.HandleAsync(new Application.UseCases.GetOperatorQueue.GetOperatorQueue(OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var single = Assert.Single(result.Value.Waiting);
        Assert.Equal(waiting.Id.Value, single.ConversationId);
        var assigned = Assert.Single(result.Value.AssignedToMe);
        Assert.Equal(assignedToMe.Id.Value, assigned.ConversationId);
    }

    // `23-78`: this handler loads full Conversation aggregates for both lists (unlike
    // GetAllConversationsForSiteHandler's own read-store projection), so the grant fields ride the
    // same row already in hand - ago-console's ConversationPage reads this same DTO
    // (useWorkspace().conversation) for its own "who/when" attribution.
    [Fact]
    public async Task HandleAsync_AnAssignedConversationWithAGrant_CarriesTheGrantFieldsOnItsSummary()
    {
        var operatorWhoGranted = new OperatorId(Guid.NewGuid());
        var assignedToMe = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        assignedToMe.AssignTo(OperatorId, Now);
        assignedToMe.MarkAttachmentUploadGrantedForTesting(operatorWhoGranted, Now);

        var (handler, conversations) = CreateHandler();
        conversations.Seed(assignedToMe);

        var result = await handler.HandleAsync(new Application.UseCases.GetOperatorQueue.GetOperatorQueue(OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var summary = Assert.Single(result.Value.AssignedToMe);
        Assert.True(summary.HasAttachmentUploadGrant);
        Assert.Equal(Now, summary.AttachmentUploadGrantedAt);
        Assert.Equal(operatorWhoGranted.Value, summary.AttachmentUploadGrantedByOperatorId);
    }

    [Fact]
    public async Task HandleAsync_AnAssignedConversationWithNoGrant_CarriesNoGrantFieldsOnItsSummary()
    {
        var assignedToMe = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        assignedToMe.AssignTo(OperatorId, Now);

        var (handler, conversations) = CreateHandler();
        conversations.Seed(assignedToMe);

        var result = await handler.HandleAsync(new Application.UseCases.GetOperatorQueue.GetOperatorQueue(OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var summary = Assert.Single(result.Value.AssignedToMe);
        Assert.False(summary.HasAttachmentUploadGrant);
        Assert.Null(summary.AttachmentUploadGrantedAt);
        Assert.Null(summary.AttachmentUploadGrantedByOperatorId);
    }

    [Fact]
    public async Task HandleAsync_WaitingConversationFromAnotherSite_IsExcluded()
    {
        var otherSite = new SiteId(Guid.NewGuid());
        var waitingElsewhere = Conversation.Start(new ConversationId(Guid.NewGuid()), otherSite, VisitorId, Now);

        var (handler, conversations) = CreateHandler();
        conversations.Seed(waitingElsewhere);

        var result = await handler.HandleAsync(new Application.UseCases.GetOperatorQueue.GetOperatorQueue(OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Waiting);
    }

    [Fact]
    public async Task HandleAsync_WithATagFilter_ReturnsOnlyTaggedConversationsInBothLists()
    {
        var taggedWaiting = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, new VisitorId(Guid.NewGuid()), Now);
        var untaggedWaiting = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, new VisitorId(Guid.NewGuid()), Now);
        var taggedAssignedToMe = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, new VisitorId(Guid.NewGuid()), Now);
        taggedAssignedToMe.AssignTo(OperatorId, Now);
        var untaggedAssignedToMe = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, new VisitorId(Guid.NewGuid()), Now);
        untaggedAssignedToMe.AssignTo(OperatorId, Now);

        var conversations = new FakeConversationRepository();
        conversations.Seed(taggedWaiting);
        conversations.Seed(untaggedWaiting);
        conversations.Seed(taggedAssignedToMe);
        conversations.Seed(untaggedAssignedToMe);

        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        var tags = new FakeTagRepository();
        var tagId = new TagId(Guid.NewGuid());
        tags.SeedAssociation(taggedWaiting.Id, tagId);
        tags.SeedAssociation(taggedAssignedToMe.Id, tagId);
        var tag = Tag.Create(tagId, SiteId, "VIP", Now);
        tags.Seed(tag);

        var handler = new GetOperatorQueueHandler(conversations, new FakeVisitorRepository(), tags, permissions);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetOperatorQueue.GetOperatorQueue(OperatorId, SiteId, [tagId]), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(taggedWaiting.Id.Value, Assert.Single(result.Value.Waiting).ConversationId);
        Assert.Equal(taggedAssignedToMe.Id.Value, Assert.Single(result.Value.AssignedToMe).ConversationId);
    }

    // `25-59`: the widened case - two tags selected must AND, not OR, matching the console's own
    // "carries every selected tag" contract. `bothTagsWaiting` is the only conversation carrying both;
    // `oneTagWaiting` carries only the first, which is exactly the case a wrongly-OR'd filter would let
    // through.
    [Fact]
    public async Task HandleAsync_WithTwoTagFilters_ReturnsOnlyConversationsCarryingBoth()
    {
        var bothTagsWaiting = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, new VisitorId(Guid.NewGuid()), Now);
        var oneTagWaiting = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, new VisitorId(Guid.NewGuid()), Now);
        var neitherTagWaiting = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, new VisitorId(Guid.NewGuid()), Now);

        var conversations = new FakeConversationRepository();
        conversations.Seed(bothTagsWaiting);
        conversations.Seed(oneTagWaiting);
        conversations.Seed(neitherTagWaiting);

        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        var tags = new FakeTagRepository();
        var vipTagId = new TagId(Guid.NewGuid());
        var billingTagId = new TagId(Guid.NewGuid());
        tags.SeedAssociation(bothTagsWaiting.Id, vipTagId);
        tags.SeedAssociation(bothTagsWaiting.Id, billingTagId);
        tags.SeedAssociation(oneTagWaiting.Id, vipTagId);
        tags.Seed(Tag.Create(vipTagId, SiteId, "VIP", Now));
        tags.Seed(Tag.Create(billingTagId, SiteId, "Billing", Now));

        var handler = new GetOperatorQueueHandler(conversations, tags, permissions);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetOperatorQueue.GetOperatorQueue(OperatorId, SiteId, [vipTagId, billingTagId]),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(bothTagsWaiting.Id.Value, Assert.Single(result.Value.Waiting).ConversationId);
    }

    // `24-10`: a blocked conversation must not appear in either half of the queue - the operator-facing
    // read this item's own Done-when names as "the conversation list" applies here too, even though
    // this list is small/unpaginated (GetOperatorQueueHandler's own remarks on why this is filtered
    // in-memory, the same way the tag filter above is).
    [Fact]
    public async Task HandleAsync_ABlockedWaitingConversation_IsExcluded()
    {
        var waiting = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        waiting.MarkBlockedForTesting(new OperatorId(Guid.NewGuid()), Now);

        var (handler, conversations) = CreateHandler();
        conversations.Seed(waiting);

        var result = await handler.HandleAsync(new Application.UseCases.GetOperatorQueue.GetOperatorQueue(OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Waiting);
    }

    [Fact]
    public async Task HandleAsync_ABlockedConversationAssignedToMe_IsExcluded()
    {
        var assignedToMe = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        assignedToMe.AssignTo(OperatorId, Now);
        assignedToMe.MarkBlockedForTesting(new OperatorId(Guid.NewGuid()), Now);

        var (handler, conversations) = CreateHandler();
        conversations.Seed(assignedToMe);

        var result = await handler.HandleAsync(new Application.UseCases.GetOperatorQueue.GetOperatorQueue(OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.AssignedToMe);
    }

    // `25-56` decision 1/5: the item's own warning made concrete - the emoji pair is keyed to the
    // visitor, not the conversation, so the *same* visitor's two conversations (one waiting, one
    // assigned to this operator) must read back the *same* pair, not two independent picks. Seeding the
    // visitor with an already-assigned pair (rather than letting a real creation handler assign one)
    // is deliberate: this test is about what GetOperatorQueueHandler reads back, not about assignment
    // itself (StartConversationHandlerTests/ReceiveChannelMessageHandlerTests own that).
    [Fact]
    public async Task HandleAsync_TheSameVisitorsTwoConversations_CarryTheIdenticalEmojiPair()
    {
        var visitor = new Visitor(VisitorId, SiteId, Now);
        visitor.AssignEmojiPair("🐳", "🌭");

        var waiting = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        var assignedToMe = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        assignedToMe.AssignTo(OperatorId, Now);

        var conversations = new FakeConversationRepository();
        conversations.Seed(waiting);
        conversations.Seed(assignedToMe);
        var visitors = new FakeVisitorRepository();
        visitors.Seed(visitor);
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        var handler = new GetOperatorQueueHandler(conversations, visitors, new FakeTagRepository(), permissions);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetOperatorQueue.GetOperatorQueue(OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var waitingSummary = Assert.Single(result.Value.Waiting);
        var assignedSummary = Assert.Single(result.Value.AssignedToMe);
        Assert.Equal("🐳", waitingSummary.EmojiCreature);
        Assert.Equal("🌭", waitingSummary.EmojiFood);
        Assert.Equal(waitingSummary.EmojiCreature, assignedSummary.EmojiCreature);
        Assert.Equal(waitingSummary.EmojiFood, assignedSummary.EmojiFood);
    }

    [Fact]
    public async Task HandleAsync_OperatorWithoutConversationReadPermission_ReturnsForbidden()
    {
        var conversations = new FakeConversationRepository();
        var permissions = new FakePermissionChecker();
        var handler = new GetOperatorQueueHandler(
            conversations, new FakeVisitorRepository(), new FakeTagRepository(), permissions);

        var result = await handler.HandleAsync(new Application.UseCases.GetOperatorQueue.GetOperatorQueue(OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    private static (GetOperatorQueueHandler Handler, FakeConversationRepository Conversations) CreateHandler()
    {
        var conversations = new FakeConversationRepository();
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        return (
            new GetOperatorQueueHandler(conversations, new FakeVisitorRepository(), new FakeTagRepository(), permissions),
            conversations);
    }
}
