using System.Text.Json;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.AcknowledgeMessageDelivered;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.AcknowledgeMessageDelivered;

public class AcknowledgeMessageDeliveredHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly VisitorId OtherVisitorId = new(Guid.NewGuid());
    private static readonly ConversationId ConversationId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        AcknowledgeMessageDeliveredHandler Handler, FakeConversationRepository Conversations,
        FakeOutboxWriter Outbox, FakeUnitOfWork UnitOfWork, Conversation Conversation, Message OperatorMessage);

    private static Fixture CreateFixture(bool assignOperator = true)
    {
        var conversation = Conversation.Start(ConversationId, SiteId, VisitorId, Now);
        if (assignOperator)
        {
            // `25-221`: a brand-new conversation starts Pending, not Waiting - graduate it with the
            // visitor's own real first message before AssignTo, which still only accepts Waiting.
            conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
            conversation.AssignTo(OperatorId, Now);
        }

        var operatorMessage = conversation.AddOperatorMessage(
            OperatorId, new MessageId(Guid.NewGuid()), new MessageBody("how can I help?"), Now);

        var conversations = new FakeConversationRepository();
        conversations.Seed(conversation);

        var outbox = new FakeOutboxWriter();
        var unitOfWork = new FakeUnitOfWork();
        var handler = new AcknowledgeMessageDeliveredHandler(conversations, unitOfWork, outbox, new FakeIdGenerator(), new FakeClock(Now));

        return new Fixture(handler, conversations, outbox, unitOfWork, conversation, operatorMessage);
    }

    [Fact]
    public async Task HandleAsync_ForTheVisitorsOwnConversation_MarksTheOperatorMessageDelivered()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.AcknowledgeMessageDelivered.AcknowledgeMessageDelivered(
                ConversationId, VisitorId, fixture.OperatorMessage.Id),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(Now, fixture.OperatorMessage.DeliveredAt);
    }

    // `25-119`'s own "the row and the event commit together" convention - only ever on an actual
    // transition, matching ModuleQuantityGrantStore's own precedent cited for this item.
    [Fact]
    public async Task HandleAsync_WhenApplied_EnqueuesMessageDelivered_AndCommitsATransaction()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.AcknowledgeMessageDelivered.AcknowledgeMessageDelivered(
                ConversationId, VisitorId, fixture.OperatorMessage.Id),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var envelope = Assert.Single(fixture.Outbox.Enqueued);
        Assert.Equal(nameof(MessageDelivered), envelope.Type);
        var contract = JsonSerializer.Deserialize<MessageDelivered>(envelope.Payload);
        Assert.Equal(ConversationId.Value, contract!.ConversationId);
        Assert.Equal(fixture.OperatorMessage.Id.Value, contract.MessageId);
        Assert.Equal(OperatorId.Value, contract.OperatorId);
        Assert.Equal(Now, contract.DeliveredAt);
        Assert.Equal(1, fixture.UnitOfWork.TransactionsBegun);
        Assert.Equal(1, fixture.UnitOfWork.TransactionsCommitted);
    }

    [Fact]
    public async Task HandleAsync_WhenTheConversationDoesNotExist_ReturnsNotFound_AndEnqueuesNothing()
    {
        var conversations = new FakeConversationRepository();
        var handler = new AcknowledgeMessageDeliveredHandler(
            conversations, new FakeUnitOfWork(), new FakeOutboxWriter(), new FakeIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.AcknowledgeMessageDelivered.AcknowledgeMessageDelivered(
                ConversationId, VisitorId, new MessageId(Guid.NewGuid())),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    // The decision this item's own brief asked to be made explicit: a mismatched visitor is refused,
    // never silently no-op'd - the identical shape GetConversationHistoryHandler.HandleAsVisitorAsync
    // already uses for the same check.
    [Fact]
    public async Task HandleAsync_ForAConversationBelongingToADifferentVisitor_ReturnsForbidden()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.AcknowledgeMessageDelivered.AcknowledgeMessageDelivered(
                ConversationId, OtherVisitorId, fixture.OperatorMessage.Id),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(fixture.Outbox.Enqueued);
        Assert.Null(fixture.OperatorMessage.DeliveredAt);
    }

    // Refused, not silently no-op'd - the same decision as the mismatched-visitor case above, for a
    // messageId that names no message of this conversation at all.
    [Fact]
    public async Task HandleAsync_ForAMessageIdThatDoesNotBelongToThisConversation_ReturnsMessageNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.AcknowledgeMessageDelivered.AcknowledgeMessageDelivered(
                ConversationId, VisitorId, new MessageId(Guid.NewGuid())),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Message.NotFound", result.Error!.Value.Code);
        Assert.Empty(fixture.Outbox.Enqueued);
    }

    // `25-119`'s own scope: a visitor's own message has no delivery-ack concept - the same
    // Message.NotFound code as a bogus id, per ConversationErrors.MessageNotFound's own remarks on
    // why one code covers both.
    [Fact]
    public async Task HandleAsync_ForTheVisitorsOwnMessage_ReturnsMessageNotFound_AndNeverThrows()
    {
        var fixture = CreateFixture();
        var visitorMessage = fixture.Conversation.AddVisitorMessage(
            VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hello?"), Now);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.AcknowledgeMessageDelivered.AcknowledgeMessageDelivered(
                ConversationId, VisitorId, visitorMessage.Id),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Message.NotFound", result.Error!.Value.Code);
    }

    // adr/0020: a redelivered ack against an already-delivered message is a harmless no-op - never a
    // second, duplicate live push for an ack that changed nothing.
    [Fact]
    public async Task HandleAsync_WhenTheMessageIsAlreadyDelivered_SucceedsAsANoOp_AndEnqueuesNothingMore()
    {
        var fixture = CreateFixture();
        await fixture.Handler.HandleAsync(
            new Application.UseCases.AcknowledgeMessageDelivered.AcknowledgeMessageDelivered(
                ConversationId, VisitorId, fixture.OperatorMessage.Id),
            CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.AcknowledgeMessageDelivered.AcknowledgeMessageDelivered(
                ConversationId, VisitorId, fixture.OperatorMessage.Id),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(fixture.Outbox.Enqueued);
        Assert.Equal(1, fixture.UnitOfWork.TransactionsBegun);
    }
}
