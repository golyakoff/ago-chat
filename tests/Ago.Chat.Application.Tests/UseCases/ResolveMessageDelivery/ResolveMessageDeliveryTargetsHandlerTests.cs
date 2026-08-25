using System.Text.Json;
using Ago.Chat.Application.Realtime;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.ResolveMessageDelivery;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using OpenTelemetry;
using OpenTelemetry.Metrics;

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
        // `5-11`: camelCase, matching SignalR's own hub-protocol default - WireJsonOptions's own doc
        // comment has the full story of why this must not be a plain JsonSerializer.Deserialize call.
        var dto = JsonSerializer.Deserialize<MessageDto>(call.PayloadJson, WireJsonOptions.Options);
        Assert.Equal("hello there", dto!.Body);
        Assert.Equal(message.Sequence, dto.Sequence);
        // `5-07`: found while building the console - a client with more than one open conversation
        // cannot route this push without knowing which conversation it belongs to.
        Assert.Equal(conversation.Id.Value, dto.ConversationId);
    }

    [Fact]
    public async Task HandleAsync_PublishesThePayloadWithCamelCasePropertyNames()
    {
        // `5-11`: found live - the fan-out path pre-serializes to a JSON string that survives a
        // JsonElement round-trip before reaching SignalR, so it must already carry the same
        // camelCase names SignalR's own hub protocol would apply to a POCO sent directly (the local
        // echo path). A PascalCase payload here means every field arrives `undefined` client-side.
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        var message = conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        var (handler, fanout) = CreateHandler(conversation);

        await handler.HandleAsync(
            new ResolveMessageDeliveryTargets(conversation.Id, message.Sequence, Guid.NewGuid()),
            CancellationToken.None);

        var call = Assert.Single(fanout.Calls);
        Assert.Contains("\"sequence\"", call.PayloadJson);
        Assert.Contains("\"authorKind\"", call.PayloadJson);
        Assert.DoesNotContain("\"Sequence\"", call.PayloadJson);
        Assert.DoesNotContain("\"AuthorKind\"", call.PayloadJson);
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

    /// <summary>
    /// `7-08`'s whole point, and the reason this instrument is not a raw "delivered to zero" count.
    /// The visitor is reading; the operator's console is closed. Both are recipients of the same
    /// fan-out and both are perfectly ordinary events on their own - it is only the *pairing* of
    /// recipient kind with presence that makes one of them worth looking at.
    ///
    /// An implementation that tagged presence from the recipient list rather than from what the
    /// registry answered - the easiest mistake here, and one that looks identical on a dashboard -
    /// would report the operator as `connected`.
    /// </summary>
    [Fact]
    public async Task HandleAsync_TagsEachRecipientByKindAndByWhetherTheRegistryHadAConnection()
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversation.AssignTo(OperatorId, Now);
        var message = conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        var (handler, fanout) = CreateHandler(conversation);
        fanout.ConnectionsByPrincipal[PrincipalKeys.ForVisitor(VisitorId)] = 2; // two tabs
        fanout.ConnectionsByPrincipal[PrincipalKeys.ForOperator(OperatorId)] = 0; // console closed

        var exportedMetrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(ChatMetrics.MeterName)
            .AddInMemoryExporter(exportedMetrics)
            .Build();

        await handler.HandleAsync(
            new ResolveMessageDeliveryTargets(conversation.Id, message.Sequence, Guid.NewGuid()),
            CancellationToken.None);
        meterProvider.ForceFlush();

        var recipients = exportedMetrics.Single(m => m.Name == ChatMetrics.DeliveryRecipientsInstrumentName);
        Assert.Equal(1, SumRecipients(recipients, PrincipalKeys.OperatorKind, ChatMetrics.AbsentPresence));
        Assert.Equal(0, SumRecipients(recipients, PrincipalKeys.OperatorKind, ChatMetrics.ConnectedPresence));
        // One point per recipient, never one per connection: the visitor's two tabs are one
        // recipient who was reachable, and the connection count lives on the fan-out's span.
        Assert.Equal(1, SumRecipients(recipients, PrincipalKeys.VisitorKind, ChatMetrics.ConnectedPresence));
        Assert.Equal(0, SumRecipients(recipients, PrincipalKeys.VisitorKind, ChatMetrics.AbsentPresence));
    }

    /// <summary>
    /// The ordinary zero, and the reason `15-03` gets to decide about alerting later with data
    /// rather than now with a guess: an unassigned conversation has exactly one recipient, and a
    /// visitor with no connection is a normal Tuesday. It must be counted, and counted as a visitor.
    /// </summary>
    [Fact]
    public async Task HandleAsync_AnUnassignedConversationWithNobodyConnected_CountsOneAbsentVisitorAndNoOperator()
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        var message = conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        var (handler, _) = CreateHandler(conversation);

        var exportedMetrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(ChatMetrics.MeterName)
            .AddInMemoryExporter(exportedMetrics)
            .Build();

        await handler.HandleAsync(
            new ResolveMessageDeliveryTargets(conversation.Id, message.Sequence, Guid.NewGuid()),
            CancellationToken.None);
        meterProvider.ForceFlush();

        var recipients = exportedMetrics.Single(m => m.Name == ChatMetrics.DeliveryRecipientsInstrumentName);
        Assert.Equal(1, SumRecipients(recipients, PrincipalKeys.VisitorKind, ChatMetrics.AbsentPresence));
        Assert.Equal(0, SumRecipients(recipients, PrincipalKeys.OperatorKind, ChatMetrics.AbsentPresence));
        Assert.Equal(0, SumRecipients(recipients, PrincipalKeys.OperatorKind, ChatMetrics.ConnectedPresence));
    }

    /// <summary>
    /// The `7-07` check applied to this instrument: a conversation with no operator must not
    /// produce an operator point at all. A counter fed from "the participants a conversation could
    /// have" rather than from "the recipients this fan-out actually resolved" would emit one, and
    /// every unassigned conversation on the platform would look like an offline operator.
    /// </summary>
    [Fact]
    public async Task HandleAsync_NeverCountsARecipientTheFanoutWasNotGiven()
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        var message = conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        var (handler, fanout) = CreateHandler(conversation);

        var exportedMetrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(ChatMetrics.MeterName)
            .AddInMemoryExporter(exportedMetrics)
            .Build();

        await handler.HandleAsync(
            new ResolveMessageDeliveryTargets(conversation.Id, message.Sequence, Guid.NewGuid()),
            CancellationToken.None);
        meterProvider.ForceFlush();

        var call = Assert.Single(fanout.Calls);
        var recipients = exportedMetrics.Single(m => m.Name == ChatMetrics.DeliveryRecipientsInstrumentName);
        var counted = SumRecipients(recipients, PrincipalKeys.VisitorKind, ChatMetrics.AbsentPresence)
            + SumRecipients(recipients, PrincipalKeys.VisitorKind, ChatMetrics.ConnectedPresence)
            + SumRecipients(recipients, PrincipalKeys.OperatorKind, ChatMetrics.AbsentPresence)
            + SumRecipients(recipients, PrincipalKeys.OperatorKind, ChatMetrics.ConnectedPresence);
        Assert.Equal(call.Recipients.Count, counted);
    }

    /// <summary>Points on <see cref="ChatMetrics.DeliveryRecipientsInstrumentName"/> matching this
    /// handler's own method tag and the given kind/presence pair.</summary>
    private static long SumRecipients(Metric metric, string recipientKind, string presence)
    {
        var total = 0L;
        foreach (ref readonly var point in metric.GetMetricPoints())
        {
            var matches = 0;
            foreach (var tag in point.Tags)
            {
                if ((tag.Key == "method" && (string?)tag.Value == "MessageReceived")
                    || (tag.Key == "recipient_kind" && (string?)tag.Value == recipientKind)
                    || (tag.Key == "presence" && (string?)tag.Value == presence))
                {
                    matches++;
                }
            }

            if (matches == 3)
            {
                total += point.GetSumLong();
            }
        }

        return total;
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
