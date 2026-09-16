using System.Text.Json;
using Ago.Chat.Application.Realtime;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.ResolveAttachmentUploadGrantDelivery;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ResolveAttachmentUploadGrantDelivery;

public class ResolveAttachmentUploadGrantDeliveryTargetsHandlerTests
{
    private static readonly Guid ConversationId = Guid.NewGuid();
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_PublishesToTheVisitorOnly_NeverAnOperator()
    {
        // `25-110`: unlike a chat message, an operator who just clicked the toggle already knows the
        // outcome from their own request's response - there is no second recipient to resolve here.
        var fanout = new FakeNodeFanoutPublisher();
        var handler = new ResolveAttachmentUploadGrantDeliveryTargetsHandler(fanout);

        var result = await handler.HandleAsync(
            new ResolveAttachmentUploadGrantDeliveryTargets(ConversationId, VisitorId.Value, true, Now, Guid.NewGuid()),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var call = Assert.Single(fanout.Calls);
        Assert.Equal(new[] { PrincipalKeys.ForVisitor(VisitorId) }, call.Recipients);
    }

    [Fact]
    public async Task HandleAsync_PublishesUnderTheAttachmentUploadGrantChangedMethodName()
    {
        var fanout = new FakeNodeFanoutPublisher();
        var handler = new ResolveAttachmentUploadGrantDeliveryTargetsHandler(fanout);

        await handler.HandleAsync(
            new ResolveAttachmentUploadGrantDeliveryTargets(ConversationId, VisitorId.Value, true, Now, Guid.NewGuid()),
            CancellationToken.None);

        var call = Assert.Single(fanout.Calls);
        Assert.Equal("AttachmentUploadGrantChanged", call.Method);
    }

    [Fact]
    public async Task HandleAsync_PublishesTheConversationIdGrantedFlagAndCorrelationId()
    {
        var fanout = new FakeNodeFanoutPublisher();
        var handler = new ResolveAttachmentUploadGrantDeliveryTargetsHandler(fanout);
        var correlationId = Guid.NewGuid();

        await handler.HandleAsync(
            new ResolveAttachmentUploadGrantDeliveryTargets(ConversationId, VisitorId.Value, true, Now, correlationId),
            CancellationToken.None);

        var call = Assert.Single(fanout.Calls);
        Assert.Equal(correlationId, call.CorrelationId);
        // `5-11`: camelCase, matching SignalR's own hub-protocol default - WireJsonOptions's own doc
        // comment has the full story of why this must not be a plain JsonSerializer.Serialize(dto).
        var dto = JsonSerializer.Deserialize<AttachmentUploadGrantChangedDto>(call.PayloadJson, WireJsonOptions.Options);
        Assert.Equal(ConversationId, dto!.ConversationId);
        Assert.True(dto.Granted);
        Assert.Equal(Now, dto.OccurredAt);
    }

    // `25-110`: revoke matters as much as grant - the item's own Done-when calls this out explicitly.
    [Fact]
    public async Task HandleAsync_ForARevoke_PublishesGrantedFalse()
    {
        var fanout = new FakeNodeFanoutPublisher();
        var handler = new ResolveAttachmentUploadGrantDeliveryTargetsHandler(fanout);

        await handler.HandleAsync(
            new ResolveAttachmentUploadGrantDeliveryTargets(ConversationId, VisitorId.Value, false, Now, Guid.NewGuid()),
            CancellationToken.None);

        var call = Assert.Single(fanout.Calls);
        var dto = JsonSerializer.Deserialize<AttachmentUploadGrantChangedDto>(call.PayloadJson, WireJsonOptions.Options);
        Assert.False(dto!.Granted);
    }

    [Fact]
    public async Task HandleAsync_PublishesThePayloadWithCamelCasePropertyNames()
    {
        var fanout = new FakeNodeFanoutPublisher();
        var handler = new ResolveAttachmentUploadGrantDeliveryTargetsHandler(fanout);

        await handler.HandleAsync(
            new ResolveAttachmentUploadGrantDeliveryTargets(ConversationId, VisitorId.Value, true, Now, Guid.NewGuid()),
            CancellationToken.None);

        var call = Assert.Single(fanout.Calls);
        Assert.Contains("\"conversationId\"", call.PayloadJson);
        Assert.Contains("\"granted\"", call.PayloadJson);
        Assert.DoesNotContain("\"ConversationId\"", call.PayloadJson);
        Assert.DoesNotContain("\"Granted\"", call.PayloadJson);
    }
}
