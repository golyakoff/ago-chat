using System.Diagnostics;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Pipeline;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `4-05`: `MessageBatchWriter` in isolation, real Postgres - the participant/state/not-found checks
/// `SendVisitorMessageHandlerTests`/`SendOperatorMessageHandlerTests` used to cover moved here along
/// with the write itself (those handler-level tests now only cover pre-enqueue checks, using a fake
/// pipeline - see their own remarks). `MessageBatchWriter.FlushAsync` stays internal to this
/// project; reached directly via this project's `InternalsVisibleTo`.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MessageBatchWriterTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FlushAsync_VisitorMessageForAConversationThatIsNotTheirs_FailsThatAckWithForbidden()
    {
        var (_, conversationId) = await SeedWaitingConversationAsync();
        var someoneElse = new VisitorId(Guid.NewGuid());

        var result = await FlushOneAsync(conversationId, MessageAuthorKind.Visitor, someoneElse.Value, "hello");

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task FlushAsync_OperatorNotAssignedToTheConversation_FailsThatAckWithForbidden()
    {
        var (siteId, _, conversationId) = await SeedAssignedConversationAsync();
        var someoneElse = new OperatorId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Operators.Add(new Operator(someoneElse, siteId, OperatorStatus.Online, capacity: 5));
            await db.SaveChangesAsync();
        }

        var result = await FlushOneAsync(conversationId, MessageAuthorKind.Operator, someoneElse.Value, "hi");

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task FlushAsync_OperatorMessage_WhenNoOperatorIsAssignedYet_FailsThatAckWithInvalidState()
    {
        var (_, conversationId) = await SeedWaitingConversationAsync();
        var operatorId = new OperatorId(Guid.NewGuid());

        var result = await FlushOneAsync(conversationId, MessageAuthorKind.Operator, operatorId.Value, "hi");

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.InvalidState", result.Error!.Value.Code);
    }

    [Fact]
    public async Task FlushAsync_VisitorMessage_WhenTheConversationIsClosed_FailsThatAckWithInvalidState()
    {
        var (visitorId, conversationId) = await SeedWaitingConversationAsync();
        await using (var db = fixture.CreateDbContext())
        {
            var conversation = await db.Conversations.FirstAsync(c => c.Id == conversationId);
            conversation.Close(Now);
            await db.SaveChangesAsync();
        }

        var result = await FlushOneAsync(conversationId, MessageAuthorKind.Visitor, visitorId, "hello");

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.InvalidState", result.Error!.Value.Code);
    }

    [Fact]
    public async Task FlushAsync_WhenTheConversationDoesNotExist_FailsThatAckWithNotFound()
    {
        var missingConversationId = new ConversationId(Guid.NewGuid());

        var result = await FlushOneAsync(missingConversationId, MessageAuthorKind.Visitor, Guid.NewGuid(), "hello");

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task FlushAsync_MultipleMessagesForTheSameConversationInOneBatch_AppliesThemInOrder_GapFreeSequence()
    {
        var (visitorId, conversationId) = await SeedWaitingConversationAsync();
        var writer = CreateWriter();

        var items = Enumerable.Range(1, 5)
            .Select(i => new InboundMessage(
                new PendingMessage(conversationId, MessageAuthorKind.Visitor, visitorId, new MessageBody($"message {i}")),
                new TaskCompletionSource<Result<int>>(TaskCreationOptions.RunContinuationsAsynchronously),
                Stopwatch.GetTimestamp()))
            .ToList();

        await writer.FlushAsync(items, CancellationToken.None);

        var sequences = new List<int>();
        foreach (var item in items)
        {
            var result = await item.Ack.Task;
            Assert.True(result.IsSuccess);
            sequences.Add(result.Value);
        }

        Assert.Equal([1, 2, 3, 4, 5], sequences);
    }

    [Fact]
    public async Task FlushAsync_OneMessagesDomainFailure_DoesNotPreventOthersInTheSameBatchFromSucceeding()
    {
        var (goodVisitorId, goodConversationId) = await SeedWaitingConversationAsync();
        var (closedVisitorId, closedConversationId) = await SeedWaitingConversationAsync();
        await using (var db = fixture.CreateDbContext())
        {
            var closed = await db.Conversations.FirstAsync(c => c.Id == closedConversationId);
            closed.Close(Now);
            await db.SaveChangesAsync();
        }

        var writer = CreateWriter();
        var goodAck = new TaskCompletionSource<Result<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var badAck = new TaskCompletionSource<Result<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var items = new List<InboundMessage>
        {
            new(new PendingMessage(goodConversationId, MessageAuthorKind.Visitor, goodVisitorId, new MessageBody("hello")), goodAck, Stopwatch.GetTimestamp()),
            new(new PendingMessage(closedConversationId, MessageAuthorKind.Visitor, closedVisitorId, new MessageBody("too late")), badAck, Stopwatch.GetTimestamp()),
        };

        await writer.FlushAsync(items, CancellationToken.None);

        var goodResult = await goodAck.Task;
        var badResult = await badAck.Task;
        Assert.True(goodResult.IsSuccess);
        Assert.Equal(1, goodResult.Value);
        Assert.True(badResult.IsFailure);
        Assert.Equal("Conversation.InvalidState", badResult.Error!.Value.Code);
    }

    [Fact]
    public async Task FlushAsync_MessageReferencingAReadyAttachment_Succeeds_AndLinksTheAttachment()
    {
        var (visitorId, conversationId) = await SeedWaitingConversationAsync();
        var attachmentId = await SeedAttachmentAsync(conversationId, AttachmentState.Ready);

        var result = await FlushOneAsync(conversationId, MessageAuthorKind.Visitor, visitorId, "look at this", attachmentId);

        Assert.True(result.IsSuccess);
        await using var verify = fixture.CreateDbContext();
        var message = await verify.Set<Message>().SingleAsync(m => m.ConversationId == conversationId);
        Assert.Equal(attachmentId, message.AttachmentId);
        var attachment = await verify.Attachments.SingleAsync(a => a.Id == attachmentId);
        Assert.Equal(message.Id, attachment.MessageId);
    }

    [Fact]
    public async Task FlushAsync_MessageReferencingAPendingAttachment_FailsThatAckWithAttachmentNotReady()
    {
        var (visitorId, conversationId) = await SeedWaitingConversationAsync();
        var attachmentId = await SeedAttachmentAsync(conversationId, AttachmentState.Pending);

        var result = await FlushOneAsync(conversationId, MessageAuthorKind.Visitor, visitorId, "look at this", attachmentId);

        Assert.True(result.IsFailure);
        Assert.Equal("Attachment.NotReady", result.Error!.Value.Code);
    }

    [Fact]
    public async Task FlushAsync_MessageReferencingADeletedAttachment_FailsThatAckWithAttachmentNotReady()
    {
        var (visitorId, conversationId) = await SeedWaitingConversationAsync();
        var attachmentId = await SeedAttachmentAsync(conversationId, AttachmentState.Deleted);

        var result = await FlushOneAsync(conversationId, MessageAuthorKind.Visitor, visitorId, "look at this", attachmentId);

        Assert.True(result.IsFailure);
        Assert.Equal("Attachment.NotReady", result.Error!.Value.Code);
    }

    [Fact]
    public async Task FlushAsync_MessageReferencingAnAttachmentFromAnotherConversation_FailsThatAckWithForbidden()
    {
        var (visitorId, conversationId) = await SeedWaitingConversationAsync();
        var (_, otherConversationId) = await SeedWaitingConversationAsync();
        var attachmentId = await SeedAttachmentAsync(otherConversationId, AttachmentState.Ready);

        var result = await FlushOneAsync(conversationId, MessageAuthorKind.Visitor, visitorId, "look at this", attachmentId);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task FlushAsync_MessageReferencingAnUnknownAttachment_FailsThatAckWithAttachmentNotFound()
    {
        var (visitorId, conversationId) = await SeedWaitingConversationAsync();
        var missingAttachmentId = new AttachmentId(Guid.NewGuid());

        var result = await FlushOneAsync(conversationId, MessageAuthorKind.Visitor, visitorId, "look at this", missingAttachmentId);

        Assert.True(result.IsFailure);
        Assert.Equal("Attachment.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task FlushAsync_OnSuccess_WritesTheOutboxRowInTheSameTransactionAsTheMessage()
    {
        var (visitorId, conversationId) = await SeedWaitingConversationAsync();

        var result = await FlushOneAsync(conversationId, MessageAuthorKind.Visitor, visitorId, "hello");
        Assert.True(result.IsSuccess);

        await using var verify = fixture.CreateDbContext();
        var message = await verify.Set<Message>().SingleAsync(m => m.ConversationId == conversationId);
        var outboxRow = await verify.Set<Ago.Platform.Persistence.Postgres.OutboxMessage>().SingleAsync(o => o.Id == message.Id.Value);
        Assert.Equal("MessageAccepted", outboxRow.Type);
        Assert.Null(outboxRow.PublishedAt);
    }

    private MessageBatchWriter CreateWriter() =>
        new(fixture.DataSource, new SystemClock(), new UuidV7Generator(), NullLogger<MessageBatchWriter>.Instance);

    private async Task<Result<int>> FlushOneAsync(
        ConversationId conversationId, MessageAuthorKind authorKind, Guid authorId, string body, AttachmentId? attachmentId = null)
    {
        var writer = CreateWriter();
        var ack = new TaskCompletionSource<Result<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new InboundMessage(
            new PendingMessage(conversationId, authorKind, authorId, new MessageBody(body), attachmentId), ack, Stopwatch.GetTimestamp());
        await writer.FlushAsync([item], CancellationToken.None);
        return await ack.Task;
    }

    private async Task<AttachmentId> SeedAttachmentAsync(ConversationId conversationId, AttachmentState state)
    {
        await using var db = fixture.CreateDbContext();
        var conversation = await db.Conversations.FirstAsync(c => c.Id == conversationId);
        var attachment = Attachment.CreatePending(
            new AttachmentId(Guid.NewGuid()), conversation.SiteId, conversationId, "site/x/conv/y/z.png", "image/png", 42, Now);
        if (state != AttachmentState.Pending)
        {
            attachment.ConfirmReady(42, "image/png", Now);
        }

        if (state == AttachmentState.Deleted)
        {
            attachment.MarkDeleted();
        }

        db.Attachments.Add(attachment);
        await db.SaveChangesAsync();
        return attachment.Id;
    }

    private async Task<(Guid VisitorId, ConversationId ConversationId)> SeedWaitingConversationAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
        await db.SaveChangesAsync();

        return (visitorId.Value, conversationId);
    }

    private async Task<(SiteId SiteId, Guid VisitorId, ConversationId ConversationId)> SeedAssignedConversationAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
        var conversation = Conversation.Start(conversationId, siteId, visitorId, Now);
        conversation.AssignTo(operatorId, Now);
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        return (siteId, visitorId.Value, conversationId);
    }
}
