using System.Text.Json;
using Ago.Chat.Application.Realtime;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.ResolveMessageDelivery;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ResolveMessageDelivery;

public class ResolveMessageDeliveryTargetsHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_UnassignedConversation_PublishesToTheVisitorOnly()
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        var message = conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        var (handler, fanout) = CreateHandler(conversation);

        var result = await handler.HandleAsync(
            new ResolveMessageDeliveryTargets(conversation.Id, message.Sequence, Guid.NewGuid()),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var call = Assert.Single(fanout.Calls);
        Assert.Equal(new[] { PrincipalKeys.ForVisitor(VisitorId) }, call.Recipients);
    }

    [Fact]
    public async Task HandleAsync_AssignedConversation_PublishesToBothVisitorAndOperator()
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversation.AssignTo(OperatorId, Now);
        var message = conversation.AddOperatorMessage(OperatorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        var (handler, fanout) = CreateHandler(conversation);

        await handler.HandleAsync(
            new ResolveMessageDeliveryTargets(conversation.Id, message.Sequence, Guid.NewGuid()),
            CancellationToken.None);

        var call = Assert.Single(fanout.Calls);
        Assert.Equal(
            new[] { PrincipalKeys.ForVisitor(VisitorId), PrincipalKeys.ForOperator(OperatorId) }.OrderBy(k => k.Value),
            call.Recipients.OrderBy(k => k.Value));
    }

    [Fact]
    public async Task HandleAsync_PublishesTheMessageContentAndCorrelationId()
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        var message = conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hello there"), Now);
        var (handler, fanout) = CreateHandler(conversation);
        var correlationId = Guid.NewGuid();

        await handler.HandleAsync(
            new ResolveMessageDeliveryTargets(conversation.Id, message.Sequence, correlationId),
            CancellationToken.None);

        var call = Assert.Single(fanout.Calls);
        Assert.Equal("MessageReceived", call.Method);
        Assert.Equal(correlationId, call.CorrelationId);
        var dto = JsonSerializer.Deserialize<MessageDto>(call.PayloadJson);
        Assert.Equal("hello there", dto!.Body);
        Assert.Equal(message.Sequence, dto.Sequence);
    }

    [Fact]
    public async Task HandleAsync_WhenConversationDoesNotExist_ReturnsNotFound_AndNeverPublishes()
    {
        var conversations = new FakeConversationRepository();
        var fanout = new FakeNodeFanoutPublisher();
        var handler = new ResolveMessageDeliveryTargetsHandler(conversations, new FakeConversationReadStore(), fanout);

        var result = await handler.HandleAsync(
            new ResolveMessageDeliveryTargets(new ConversationId(Guid.NewGuid()), 1, Guid.NewGuid()),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
        Assert.Empty(fanout.Calls);
    }

    private static (ResolveMessageDeliveryTargetsHandler Handler, FakeNodeFanoutPublisher Fanout) CreateHandler(Conversation conversation)
    {
        var conversations = new FakeConversationRepository();
        conversations.Seed(conversation);
        var readStore = new FakeConversationReadStore();
        readStore.Seed(conversation);
        var fanout = new FakeNodeFanoutPublisher();
        return (new ResolveMessageDeliveryTargetsHandler(conversations, readStore, fanout), fanout);
    }
}
