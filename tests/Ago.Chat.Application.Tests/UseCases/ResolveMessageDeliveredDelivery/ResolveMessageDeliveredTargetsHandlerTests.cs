using System.Text.Json;
using Ago.Chat.Application.Realtime;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.ResolveMessageDeliveredDelivery;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ResolveMessageDeliveredDelivery;

public class ResolveMessageDeliveredTargetsHandlerTests
{
    private static readonly Guid ConversationId = Guid.NewGuid();
    private static readonly Guid MessageId = Guid.NewGuid();
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // `25-119`: unlike a chat message's own resolver, there is only ever one recipient here - the
    // message's own author - and it is already on the event, so this needs no conversation load at all.
    [Fact]
    public async Task HandleAsync_PublishesToTheAuthoringOperatorOnly()
    {
        var fanout = new FakeNodeFanoutPublisher();
        var handler = new ResolveMessageDeliveredTargetsHandler(fanout);

        var result = await handler.HandleAsync(
            new ResolveMessageDeliveredTargets(ConversationId, MessageId, OperatorId.Value, Now, Guid.NewGuid()),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var call = Assert.Single(fanout.Calls);
        Assert.Equal(new[] { PrincipalKeys.ForOperator(OperatorId) }, call.Recipients);
    }

    [Fact]
    public async Task HandleAsync_PublishesUnderTheMessageDeliveredMethodName()
    {
        var fanout = new FakeNodeFanoutPublisher();
        var handler = new ResolveMessageDeliveredTargetsHandler(fanout);

        await handler.HandleAsync(
            new ResolveMessageDeliveredTargets(ConversationId, MessageId, OperatorId.Value, Now, Guid.NewGuid()),
            CancellationToken.None);

        var call = Assert.Single(fanout.Calls);
        Assert.Equal("MessageDelivered", call.Method);
    }

    [Fact]
    public async Task HandleAsync_PublishesTheConversationIdMessageIdDeliveredAtAndCorrelationId()
    {
        var fanout = new FakeNodeFanoutPublisher();
        var handler = new ResolveMessageDeliveredTargetsHandler(fanout);
        var correlationId = Guid.NewGuid();

        await handler.HandleAsync(
            new ResolveMessageDeliveredTargets(ConversationId, MessageId, OperatorId.Value, Now, correlationId),
            CancellationToken.None);

        var call = Assert.Single(fanout.Calls);
        Assert.Equal(correlationId, call.CorrelationId);
        // `5-11`: camelCase, matching SignalR's own hub-protocol default.
        var dto = JsonSerializer.Deserialize<MessageDeliveredDto>(call.PayloadJson, WireJsonOptions.Options);
        Assert.Equal(ConversationId, dto!.ConversationId);
        Assert.Equal(MessageId, dto.MessageId);
        Assert.Equal(Now, dto.DeliveredAt);
    }

    [Fact]
    public async Task HandleAsync_PublishesThePayloadWithCamelCasePropertyNames()
    {
        var fanout = new FakeNodeFanoutPublisher();
        var handler = new ResolveMessageDeliveredTargetsHandler(fanout);

        await handler.HandleAsync(
            new ResolveMessageDeliveredTargets(ConversationId, MessageId, OperatorId.Value, Now, Guid.NewGuid()),
            CancellationToken.None);

        var call = Assert.Single(fanout.Calls);
        Assert.Contains("\"conversationId\"", call.PayloadJson);
        Assert.Contains("\"messageId\"", call.PayloadJson);
        Assert.DoesNotContain("\"ConversationId\"", call.PayloadJson);
        Assert.DoesNotContain("\"MessageId\"", call.PayloadJson);
    }
}
