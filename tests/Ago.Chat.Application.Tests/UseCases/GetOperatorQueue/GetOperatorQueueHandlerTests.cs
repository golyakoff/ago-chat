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
        // `25-221`: a brand-new conversation starts Pending, not Waiting - each of these three needs
        // the visitor's own real first message before it is genuinely Waiting (or assignable at all).
        var waiting = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        waiting.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        var assignedToMe = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, new VisitorId(Guid.NewGuid()), Now);
        assignedToMe.AddVisitorMessage(assignedToMe.VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        assignedToMe.AssignTo(OperatorId, Now);
        var assignedToSomeoneElse = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, new VisitorId(Guid.NewGuid()), Now);
        assignedToSomeoneElse.AddVisitorMessage(assignedToSomeoneElse.VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
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
        assignedToMe.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
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
        assignedToMe.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
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
        waitingElsewhere.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);

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
        taggedWaiting.AddVisitorMessage(taggedWaiting.VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        var untaggedWaiting = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, new VisitorId(Guid.NewGuid()), Now);
        untaggedWaiting.AddVisitorMessage(untaggedWaiting.VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        var taggedAssignedToMe = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, new VisitorId(Guid.NewGuid()), Now);
        taggedAssignedToMe.AddVisitorMessage(taggedAssignedToMe.VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        taggedAssignedToMe.AssignTo(OperatorId, Now);
        var untaggedAssignedToMe = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, new VisitorId(Guid.NewGuid()), Now);
        untaggedAssignedToMe.AddVisitorMessage(untaggedAssignedToMe.VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
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

        var handler = new GetOperatorQueueHandler(
            conversations, new FakeVisitorRepository(), tags, permissions, new FakeVisitorContactDetailRepository(),
            new FakeConversationReadStore());

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
        bothTagsWaiting.AddVisitorMessage(bothTagsWaiting.VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        var oneTagWaiting = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, new VisitorId(Guid.NewGuid()), Now);
        oneTagWaiting.AddVisitorMessage(oneTagWaiting.VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        var neitherTagWaiting = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, new VisitorId(Guid.NewGuid()), Now);
        neitherTagWaiting.AddVisitorMessage(neitherTagWaiting.VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);

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

        var handler = new GetOperatorQueueHandler(
            conversations, new FakeVisitorRepository(), tags, permissions, new FakeVisitorContactDetailRepository(),
            new FakeConversationReadStore());

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
        waiting.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
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
        assignedToMe.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        assignedToMe.AssignTo(OperatorId, Now);
        assignedToMe.MarkBlockedForTesting(new OperatorId(Guid.NewGuid()), Now);

        var (handler, conversations) = CreateHandler();
        conversations.Seed(assignedToMe);

        var result = await handler.HandleAsync(new Application.UseCases.GetOperatorQueue.GetOperatorQueue(OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.AssignedToMe);
    }

    // `23-69`/`23-77`: the identical "an operator never sees it" filter right above, restated for the
    // second, separate flag `StartConversationHandler` stamps on a brand-new conversation for a
    // restricted visitor - Conversation.RoutingSuppressedAt's own remarks explain why this is not
    // IsBlocked itself.
    [Fact]
    public async Task HandleAsync_ARoutingSuppressedWaitingConversation_IsExcluded()
    {
        var waiting = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now, suppressRouting: true);
        waiting.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);

        var (handler, conversations) = CreateHandler();
        conversations.Seed(waiting);

        var result = await handler.HandleAsync(new Application.UseCases.GetOperatorQueue.GetOperatorQueue(OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Waiting);
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
        waiting.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        var assignedToMe = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        assignedToMe.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        assignedToMe.AssignTo(OperatorId, Now);

        var conversations = new FakeConversationRepository();
        conversations.Seed(waiting);
        conversations.Seed(assignedToMe);
        var visitors = new FakeVisitorRepository();
        visitors.Seed(visitor);
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        var handler = new GetOperatorQueueHandler(
            conversations, visitors, new FakeTagRepository(), permissions, new FakeVisitorContactDetailRepository(),
            new FakeConversationReadStore());

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

    // `25-56`'s own second half, the identical case as the emoji-pair test right above but for
    // VisitorName: the same visitor's two conversations must carry the same name, sourced from
    // IVisitorContactDetailRepository rather than Visitor itself.
    [Fact]
    public async Task HandleAsync_TheSameVisitorsTwoConversations_CarryTheIdenticalVisitorName()
    {
        var waiting = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        waiting.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        var assignedToMe = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        assignedToMe.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        assignedToMe.AssignTo(OperatorId, Now);

        var conversations = new FakeConversationRepository();
        conversations.Seed(waiting);
        conversations.Seed(assignedToMe);
        var contactDetails = new FakeVisitorContactDetailRepository();
        contactDetails.Seed(VisitorContactDetail.RecordFromVisitor(
            new VisitorContactDetailId(Guid.NewGuid()), VisitorId, VisitorContactDetailKind.Name, "Иван Иванов", Now));
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        var handler = new GetOperatorQueueHandler(
            conversations, new FakeVisitorRepository(), new FakeTagRepository(), permissions, contactDetails,
            new FakeConversationReadStore());

        var result = await handler.HandleAsync(
            new Application.UseCases.GetOperatorQueue.GetOperatorQueue(OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var waitingSummary = Assert.Single(result.Value.Waiting);
        var assignedSummary = Assert.Single(result.Value.AssignedToMe);
        Assert.Equal("Иван Иванов", waitingSummary.VisitorName);
        Assert.Equal(waitingSummary.VisitorName, assignedSummary.VisitorName);
    }

    // The ordinary case - decision 4's "repeats are acceptable, the name is what actually
    // disambiguates" only helps once a name exists; most visitors never give one.
    [Fact]
    public async Task HandleAsync_AVisitorWhoNeverGaveAName_CarriesANullVisitorName()
    {
        var (handler, conversations) = CreateHandler();
        var assignedToMe = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        assignedToMe.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        assignedToMe.AssignTo(OperatorId, Now);
        conversations.Seed(assignedToMe);

        var result = await handler.HandleAsync(new Application.UseCases.GetOperatorQueue.GetOperatorQueue(OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(Assert.Single(result.Value.AssignedToMe).VisitorName);
    }

    // `IVisitorContactDetailRepository.GetNamesForVisitorsAsync`'s own documented reduction: no unique
    // index on (visitor, kind) means more than one Name-kind row is possible (a visitor's own form
    // submitted twice, once with a typo) - the most recently recorded one must win.
    [Fact]
    public async Task HandleAsync_AVisitorWithTwoNameRows_CarriesTheMostRecentlyRecordedOne()
    {
        var assignedToMe = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        assignedToMe.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        assignedToMe.AssignTo(OperatorId, Now);

        var conversations = new FakeConversationRepository();
        conversations.Seed(assignedToMe);
        var contactDetails = new FakeVisitorContactDetailRepository();
        contactDetails.Seed(VisitorContactDetail.RecordFromVisitor(
            new VisitorContactDetailId(Guid.NewGuid()), VisitorId, VisitorContactDetailKind.Name, "Ivan",
            Now.AddMinutes(-10)));
        contactDetails.Seed(VisitorContactDetail.RecordFromVisitor(
            new VisitorContactDetailId(Guid.NewGuid()), VisitorId, VisitorContactDetailKind.Name, "Иван Иванов", Now));
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        var handler = new GetOperatorQueueHandler(
            conversations, new FakeVisitorRepository(), new FakeTagRepository(), permissions, contactDetails,
            new FakeConversationReadStore());

        var result = await handler.HandleAsync(
            new Application.UseCases.GetOperatorQueue.GetOperatorQueue(OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Иван Иванов", Assert.Single(result.Value.AssignedToMe).VisitorName);
    }

    // `26-29`: the ordinary case - the latest visitor message's own body and timestamp ride the
    // summary, read from IConversationReadStore rather than the Conversation.Messages navigation
    // (this handler's own remarks explain why: an unbounded read on a screen the console polls).
    [Fact]
    public async Task HandleAsync_ALatestVisitorMessage_PopulatesThePreviewAndTimestamp()
    {
        var assignedToMe = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        assignedToMe.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        var lastMessageAt = Now.AddMinutes(5);
        assignedToMe.AssignTo(OperatorId, Now);
        assignedToMe.AddOperatorMessage(OperatorId, new MessageId(Guid.NewGuid()), new MessageBody("how can I help?"), lastMessageAt);

        var conversations = new FakeConversationRepository();
        conversations.Seed(assignedToMe);
        var readStore = new FakeConversationReadStore();
        readStore.Seed(assignedToMe);
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        var handler = new GetOperatorQueueHandler(
            conversations, new FakeVisitorRepository(), new FakeTagRepository(), permissions,
            new FakeVisitorContactDetailRepository(), readStore);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetOperatorQueue.GetOperatorQueue(OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var summary = Assert.Single(result.Value.AssignedToMe);
        Assert.Equal("how can I help?", summary.LastMessagePreview);
        Assert.Equal(lastMessageAt, summary.LastMessageAt);
    }

    // `26-29`: "a system message counts as the last message if it genuinely is the last one" - hiding
    // it would make the timestamp and the text disagree about whether anything was said since.
    [Fact]
    public async Task HandleAsync_TheLatestMessageIsSystemAuthored_StillPopulatesThePreviewAndTimestamp()
    {
        var assignedToMe = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        assignedToMe.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        assignedToMe.AssignTo(OperatorId, Now);
        var lastMessageAt = Now.AddMinutes(5);
        assignedToMe.AddSystemMessage(new MessageId(Guid.NewGuid()), new MessageBody("We are back online."), lastMessageAt);

        var conversations = new FakeConversationRepository();
        conversations.Seed(assignedToMe);
        var readStore = new FakeConversationReadStore();
        readStore.Seed(assignedToMe);
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        var handler = new GetOperatorQueueHandler(
            conversations, new FakeVisitorRepository(), new FakeTagRepository(), permissions,
            new FakeVisitorContactDetailRepository(), readStore);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetOperatorQueue.GetOperatorQueue(OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var summary = Assert.Single(result.Value.AssignedToMe);
        Assert.Equal("We are back online.", summary.LastMessagePreview);
        Assert.Equal(lastMessageAt, summary.LastMessageAt);
    }

    // `26-29`: the stated truncation ceiling - a body well past 80 characters comes back cut to
    // exactly that length (79 real characters plus the ellipsis), never the full text.
    [Fact]
    public async Task HandleAsync_ALongLatestMessageBody_IsTruncatedToTheStatedMaximum()
    {
        var longBody = string.Concat(Enumerable.Repeat("0123456789", 20)); // 200 characters
        var assignedToMe = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        assignedToMe.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody(longBody), Now);
        assignedToMe.AssignTo(OperatorId, Now);

        var conversations = new FakeConversationRepository();
        conversations.Seed(assignedToMe);
        var readStore = new FakeConversationReadStore();
        readStore.Seed(assignedToMe);
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        var handler = new GetOperatorQueueHandler(
            conversations, new FakeVisitorRepository(), new FakeTagRepository(), permissions,
            new FakeVisitorContactDetailRepository(), readStore);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetOperatorQueue.GetOperatorQueue(OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var preview = Assert.Single(result.Value.AssignedToMe).LastMessagePreview;
        Assert.NotNull(preview);
        Assert.Equal(80, preview!.Length);
        Assert.EndsWith("…", preview);
        Assert.StartsWith(longBody[..79], preview);
    }

    // `26-29`: a message that references an attachment has no sensible plain-text body to preview -
    // the backend cannot tell a real caption apart from a client-supplied placeholder standing in for
    // a file. The timestamp still moves forward regardless.
    [Fact]
    public async Task HandleAsync_TheLatestMessageHasAnAttachment_PreviewIsNullButTimestampIsSet()
    {
        var assignedToMe = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        assignedToMe.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        assignedToMe.AssignTo(OperatorId, Now);
        var lastMessageAt = Now.AddMinutes(5);
        assignedToMe.AddOperatorMessage(
            OperatorId, new MessageId(Guid.NewGuid()), new MessageBody("see attached"), lastMessageAt,
            attachmentId: new AttachmentId(Guid.NewGuid()));

        var conversations = new FakeConversationRepository();
        conversations.Seed(assignedToMe);
        var readStore = new FakeConversationReadStore();
        readStore.Seed(assignedToMe);
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        var handler = new GetOperatorQueueHandler(
            conversations, new FakeVisitorRepository(), new FakeTagRepository(), permissions,
            new FakeVisitorContactDetailRepository(), readStore);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetOperatorQueue.GetOperatorQueue(OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var summary = Assert.Single(result.Value.AssignedToMe);
        Assert.Null(summary.LastMessagePreview);
        Assert.Equal(lastMessageAt, summary.LastMessageAt);
    }

    // `26-29`: the identical "no sensible plain-text preview" treatment for structured content (a
    // module step, or any other non-prose MessageContentKind) - Message.Body stays mandatory even
    // here, but this DTO deliberately does not trust it as a one-line summary.
    [Fact]
    public async Task HandleAsync_TheLatestMessageCarriesStructuredContent_PreviewIsNullButTimestampIsSet()
    {
        var assignedToMe = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        assignedToMe.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        assignedToMe.AssignTo(OperatorId, Now);
        var lastMessageAt = Now.AddMinutes(5);
        var content = MessageContent.Create(new MessageContentKind("booking.confirmation"));
        assignedToMe.AddSystemMessage(
            new MessageId(Guid.NewGuid()), new MessageBody("Your booking is confirmed for Tuesday."), lastMessageAt,
            content: content);

        var conversations = new FakeConversationRepository();
        conversations.Seed(assignedToMe);
        var readStore = new FakeConversationReadStore();
        readStore.Seed(assignedToMe);
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        var handler = new GetOperatorQueueHandler(
            conversations, new FakeVisitorRepository(), new FakeTagRepository(), permissions,
            new FakeVisitorContactDetailRepository(), readStore);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetOperatorQueue.GetOperatorQueue(OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var summary = Assert.Single(result.Value.AssignedToMe);
        Assert.Null(summary.LastMessagePreview);
        Assert.Equal(lastMessageAt, summary.LastMessageAt);
    }

    // `26-29`'s own Done-when: "a conversation with no messages at all sends both as null, and no
    // client is required to guess." A conversation only ever reaches this handler's two lists once it
    // has at least one real message (`GetWaitingForSiteAsync`/`GetAssignedToOperatorAsync` both filter
    // to a state a message caused, `25-221`), so this proves the defensive branch directly: a read
    // store that genuinely has nothing for this id (exactly what the real query returns for an id with
    // no `messages` rows - `ConversationReadStoreTests`' own integration coverage proves that half)
    // must not surface a null-reference or a placeholder, only two absent fields.
    [Fact]
    public async Task HandleAsync_TheReadStoreHasNoLatestMessageForThisConversation_SendsBothFieldsNull()
    {
        var assignedToMe = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        assignedToMe.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        assignedToMe.AssignTo(OperatorId, Now);

        var conversations = new FakeConversationRepository();
        conversations.Seed(assignedToMe);
        // Deliberately not seeded into the read store - mirrors what the real query returns for an id
        // with no `messages` rows at all (absent, not a placeholder).
        var readStore = new FakeConversationReadStore();
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        var handler = new GetOperatorQueueHandler(
            conversations, new FakeVisitorRepository(), new FakeTagRepository(), permissions,
            new FakeVisitorContactDetailRepository(), readStore);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetOperatorQueue.GetOperatorQueue(OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var summary = Assert.Single(result.Value.AssignedToMe);
        Assert.Null(summary.LastMessagePreview);
        Assert.Null(summary.LastMessageAt);
    }

    [Fact]
    public async Task HandleAsync_OperatorWithoutConversationReadPermission_ReturnsForbidden()
    {
        var conversations = new FakeConversationRepository();
        var permissions = new FakePermissionChecker();
        var handler = new GetOperatorQueueHandler(
            conversations, new FakeVisitorRepository(), new FakeTagRepository(), permissions,
            new FakeVisitorContactDetailRepository(), new FakeConversationReadStore());

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
            new GetOperatorQueueHandler(
                conversations, new FakeVisitorRepository(), new FakeTagRepository(), permissions,
                new FakeVisitorContactDetailRepository(), new FakeConversationReadStore()),
            conversations);
    }
}
