using System.Text.Json;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Application.Realtime;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.NotifyOperatorDevices;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Ago.Chat.Application.Tests.UseCases.NotifyOperatorDevices;

/// <summary>
/// `26-05`/`push-notifications.md`'s own "Fan-out" section, at the handler level - the fast, no-infra
/// half of this item's own proof. `OperatorPushFanOutEndToEndTests` (`Ago.Chat.Integration.Tests`)
/// covers the two `Ago.Chat.Worker` consumers this handler sits behind: their own distinct queue/DLQ
/// naming, and that adding `OperatorMessagePushConsumer` as a new `MessageAccepted` subscriber does not
/// disrupt an existing sibling.
/// </summary>
public class NotifyOperatorDevicesHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAssignmentAsync_SendsOnePushPerActiveDeviceOfTheAssignedOperator()
    {
        var (handler, devices, _, pushSender) = CreateHandler();
        devices.Seed(RegisterDevice(OperatorId, "install-1", "token-1"));
        devices.Seed(RegisterDevice(OperatorId, "install-2", "token-2"));

        var conversationId = new ConversationId(Guid.NewGuid());
        var result = await handler.HandleAssignmentAsync(
            new NotifyOperatorDeviceForAssignment(conversationId, VisitorId, OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, pushSender.Calls.Count);
        Assert.Equal(["token-1", "token-2"], pushSender.Calls.Select(c => c.DeviceToken).OrderBy(t => t));
    }

    /// <summary>`alertTextFor("assigned", ...)`'s own English text, ported rather than duplicated in
    /// spirit - see `NotifyOperatorDevicesHandler`'s own remarks on why there is no operator locale to
    /// route on server-side.</summary>
    [Fact]
    public async Task HandleAssignmentAsync_UsesAlertTextForsAssignedTextAndTheConsoleTag()
    {
        var (handler, devices, _, pushSender) = CreateHandler();
        devices.Seed(RegisterDevice(OperatorId, "install-1", "token-1"));
        var conversationId = new ConversationId(Guid.NewGuid());

        await handler.HandleAssignmentAsync(
            new NotifyOperatorDeviceForAssignment(conversationId, VisitorId, OperatorId), CancellationToken.None);

        var call = Assert.Single(pushSender.Calls);
        Assert.Equal("New conversation assigned", call.Title);
        Assert.Equal($"Visitor {VisitorId.Value.ToString()[..8]} is waiting for you.", call.Body);
        Assert.DoesNotContain("hi", call.Body, StringComparison.Ordinal); // never a message body - there is none to leak here
        Assert.Equal($"ago-conversation-{conversationId.Value}", call.GroupKey);
    }

    /// <summary>`push-notifications.md`'s own Done-when: the payload carries the conversation id so
    /// `26-18` can build `useAlerts.ts`'s own `ago-conversation-{id}` tag - no message id for an
    /// assignment, since there is no message.</summary>
    [Fact]
    public async Task HandleAssignmentAsync_DataCarriesOnlyTheConversationId()
    {
        var (handler, devices, _, pushSender) = CreateHandler();
        devices.Seed(RegisterDevice(OperatorId, "install-1", "token-1"));
        var conversationId = new ConversationId(Guid.NewGuid());

        await handler.HandleAssignmentAsync(
            new NotifyOperatorDeviceForAssignment(conversationId, VisitorId, OperatorId), CancellationToken.None);

        var call = Assert.Single(pushSender.Calls);
        Assert.Equal(conversationId.Value.ToString(), call.Data["conversationId"]);
        Assert.False(call.Data.ContainsKey("messageId"));
    }

    [Fact]
    public async Task HandleAssignmentAsync_NoActiveDevices_NeverCallsThePushSender()
    {
        var (handler, _, _, pushSender) = CreateHandler();

        var result = await handler.HandleAssignmentAsync(
            new NotifyOperatorDeviceForAssignment(new ConversationId(Guid.NewGuid()), VisitorId, OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(pushSender.Calls);
    }

    [Fact]
    public async Task HandleAssignmentAsync_ARevokedDevice_IsNeverSentTo()
    {
        var (handler, devices, _, pushSender) = CreateHandler();
        var revoked = RegisterDevice(OperatorId, "install-1", "token-1");
        revoked.Revoke(Now);
        devices.Seed(revoked);

        await handler.HandleAssignmentAsync(
            new NotifyOperatorDeviceForAssignment(new ConversationId(Guid.NewGuid()), VisitorId, OperatorId), CancellationToken.None);

        Assert.Empty(pushSender.Calls);
    }

    [Fact]
    public async Task HandleAssignmentAsync_NoActiveDevices_RecordsSuppressedWithNoDevicesReason()
    {
        var (handler, _, _, _) = CreateHandler();
        using var listener = ListenToPushMetrics();

        await handler.HandleAssignmentAsync(
            new NotifyOperatorDeviceForAssignment(new ConversationId(Guid.NewGuid()), VisitorId, OperatorId), CancellationToken.None);
        listener.ForceFlush();

        AssertSuppressedReason(listener.Metrics, "no_devices");
    }

    /// <summary>`adr/0179`'s own "covered for free" - `ConversationTransferredMapper` maps
    /// `Ago.Chat.Domain.ConversationTransferred` onto the identical `ConversationAssignedToOperator`
    /// contract an initial assignment produces, so this test runs the real production mapper, not an
    /// assumption about its shape, and feeds its real output through the real handler - the
    /// push-notifications.md Done-when's own "proven by a real test, not inferred from the mapper
    /// existing."</summary>
    [Fact]
    public async Task Transfer_MappedThroughTheRealConversationTransferredMapper_ProducesAPushToTheNewAssignee()
    {
        var fromOperatorId = new OperatorId(Guid.NewGuid());
        var toOperatorId = new OperatorId(Guid.NewGuid());
        var (handler, devices, _, pushSender) = CreateHandler();
        devices.Seed(RegisterDevice(fromOperatorId, "install-old", "token-old"));
        devices.Seed(RegisterDevice(toOperatorId, "install-new", "token-new"));

        var conversationId = new ConversationId(Guid.NewGuid());
        var domainEvent = new ConversationTransferred(conversationId, fromOperatorId, toOperatorId, Now);
        var envelope = ConversationTransferredMapper.ToEnvelope(domainEvent, SiteId, VisitorId, new UuidV7Generator());
        var contract = JsonSerializer.Deserialize<ConversationAssignedToOperator>(envelope.Payload)!;

        await handler.HandleAssignmentAsync(
            new NotifyOperatorDeviceForAssignment(
                new ConversationId(contract.ConversationId), new VisitorId(contract.VisitorId), new OperatorId(contract.OperatorId)),
            CancellationToken.None);

        var call = Assert.Single(pushSender.Calls);
        Assert.Equal("token-new", call.DeviceToken);
    }

    [Fact]
    public async Task HandleMessageAsync_VisitorAuthored_AssignedConversation_SendsPushToTheAssignedOperator()
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversation.AssignTo(OperatorId, Now);
        var (handler, devices, conversations, pushSender) = CreateHandler();
        conversations.Seed(conversation);
        devices.Seed(RegisterDevice(OperatorId, "install-1", "token-1"));
        var messageId = new MessageId(Guid.NewGuid());

        var result = await handler.HandleMessageAsync(
            new NotifyOperatorDeviceForMessage(conversation.Id, messageId, nameof(MessageAuthorKind.Visitor)), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var call = Assert.Single(pushSender.Calls);
        Assert.Equal("token-1", call.DeviceToken);
        Assert.Equal("New message", call.Title);
        Assert.Equal($"Visitor {VisitorId.Value.ToString()[..8]} sent a message.", call.Body);
    }

    /// <summary>`push-notifications.md`'s own Done-when: the payload carries both the conversation id
    /// and the message id, so `26-18` can dedupe by message id in addition to the notification tag.</summary>
    [Fact]
    public async Task HandleMessageAsync_VisitorAuthored_DataCarriesBothConversationIdAndMessageId()
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversation.AssignTo(OperatorId, Now);
        var (handler, devices, conversations, pushSender) = CreateHandler();
        conversations.Seed(conversation);
        devices.Seed(RegisterDevice(OperatorId, "install-1", "token-1"));
        var messageId = new MessageId(Guid.NewGuid());

        await handler.HandleMessageAsync(
            new NotifyOperatorDeviceForMessage(conversation.Id, messageId, nameof(MessageAuthorKind.Visitor)), CancellationToken.None);

        var call = Assert.Single(pushSender.Calls);
        Assert.Equal(conversation.Id.Value.ToString(), call.Data["conversationId"]);
        Assert.Equal(messageId.Value.ToString(), call.Data["messageId"]);
        Assert.Equal($"ago-conversation-{conversation.Id.Value}", call.GroupKey);
    }

    /// <summary>`alerts.ts`'s own first filter, verbatim: an operator's own echoed-back send is not
    /// news. This is the core of `push-notifications.md`'s "a message-accepted event from the visitor
    /// side fires push while one from the operator side does not."</summary>
    [Fact]
    public async Task HandleMessageAsync_OperatorAuthored_NeverSends()
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversation.AssignTo(OperatorId, Now);
        var (handler, devices, conversations, pushSender) = CreateHandler();
        conversations.Seed(conversation);
        devices.Seed(RegisterDevice(OperatorId, "install-1", "token-1"));

        var result = await handler.HandleMessageAsync(
            new NotifyOperatorDeviceForMessage(conversation.Id, new MessageId(Guid.NewGuid()), nameof(MessageAuthorKind.Operator)),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(pushSender.Calls);
    }

    /// <summary>Neither of `MessageAuthorKind`'s other two members (`System`/`AutoGreeting`) is a
    /// visitor either - the filter is "was this a visitor," not "was this not an operator."</summary>
    [Theory]
    [InlineData(nameof(MessageAuthorKind.System))]
    [InlineData(nameof(MessageAuthorKind.AutoGreeting))]
    public async Task HandleMessageAsync_NonVisitorAuthorKinds_NeverSend(string authorKind)
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversation.AssignTo(OperatorId, Now);
        var (handler, devices, conversations, pushSender) = CreateHandler();
        conversations.Seed(conversation);
        devices.Seed(RegisterDevice(OperatorId, "install-1", "token-1"));

        await handler.HandleMessageAsync(
            new NotifyOperatorDeviceForMessage(conversation.Id, new MessageId(Guid.NewGuid()), authorKind), CancellationToken.None);

        Assert.Empty(pushSender.Calls);
    }

    [Fact]
    public async Task HandleMessageAsync_OperatorAuthored_RecordsSuppressedWithNotVisitorReason()
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversation.AssignTo(OperatorId, Now);
        var (handler, devices, conversations, _) = CreateHandler();
        conversations.Seed(conversation);
        devices.Seed(RegisterDevice(OperatorId, "install-1", "token-1"));
        using var listener = ListenToPushMetrics();

        await handler.HandleMessageAsync(
            new NotifyOperatorDeviceForMessage(conversation.Id, new MessageId(Guid.NewGuid()), nameof(MessageAuthorKind.Operator)),
            CancellationToken.None);
        listener.ForceFlush();

        AssertSuppressedReason(listener.Metrics, "not_visitor");
    }

    [Fact]
    public async Task HandleMessageAsync_VisitorAuthored_UnassignedConversation_NeverSends()
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        var (handler, _, conversations, pushSender) = CreateHandler();
        conversations.Seed(conversation);

        var result = await handler.HandleMessageAsync(
            new NotifyOperatorDeviceForMessage(conversation.Id, new MessageId(Guid.NewGuid()), nameof(MessageAuthorKind.Visitor)),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(pushSender.Calls);
    }

    [Fact]
    public async Task HandleMessageAsync_VisitorAuthored_UnassignedConversation_RecordsSuppressedWithUnassignedReason()
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        var (handler, _, conversations, _) = CreateHandler();
        conversations.Seed(conversation);
        using var listener = ListenToPushMetrics();

        await handler.HandleMessageAsync(
            new NotifyOperatorDeviceForMessage(conversation.Id, new MessageId(Guid.NewGuid()), nameof(MessageAuthorKind.Visitor)),
            CancellationToken.None);
        listener.ForceFlush();

        AssertSuppressedReason(listener.Metrics, "unassigned");
    }

    [Fact]
    public async Task HandleMessageAsync_ConversationDoesNotExist_ReturnsNotFound_AndNeverSends()
    {
        var (handler, _, _, pushSender) = CreateHandler();

        var result = await handler.HandleMessageAsync(
            new NotifyOperatorDeviceForMessage(new ConversationId(Guid.NewGuid()), new MessageId(Guid.NewGuid()), nameof(MessageAuthorKind.Visitor)),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
        Assert.Empty(pushSender.Calls);
    }

    [Fact]
    public async Task HandleAsync_WhenTheProviderReportsTheTokenGone_RevokesTheDeviceRow()
    {
        var (handler, devices, _, pushSender) = CreateHandlerWithOutcome(new PushSendOutcome.TokenGone("RuStore 404 NOT_FOUND"));
        var device = RegisterDevice(OperatorId, "install-1", "token-1");
        devices.Seed(device);

        await handler.HandleAssignmentAsync(
            new NotifyOperatorDeviceForAssignment(new ConversationId(Guid.NewGuid()), VisitorId, OperatorId), CancellationToken.None);

        var saved = await devices.FindAsync(OperatorId, "install-1", CancellationToken.None);
        Assert.NotNull(saved!.RevokedAt);
        Assert.Single(pushSender.Calls);
    }

    /// <summary>`26-04`'s own most insistent Done-when, restated for this handler: a `TransientFailure`
    /// (RuStore's own credential-fault outcomes - `401`/`403`/`429`/`500`) must never revoke a device
    /// row. Getting this backwards would empty the whole table the first time the service token was
    /// ever wrong (`IPushSender.SendAsync`'s own remarks).</summary>
    [Fact]
    public async Task HandleAsync_WhenTheProviderReportsATransientFailure_RecordsItButNeverRevokesTheDevice()
    {
        var (handler, devices, _, _) = CreateHandlerWithOutcome(new PushSendOutcome.TransientFailure("RuStore 403 PERMISSION_DENIED"));
        var device = RegisterDevice(OperatorId, "install-1", "token-1");
        devices.Seed(device);

        await handler.HandleAssignmentAsync(
            new NotifyOperatorDeviceForAssignment(new ConversationId(Guid.NewGuid()), VisitorId, OperatorId), CancellationToken.None);

        var saved = await devices.FindAsync(OperatorId, "install-1", CancellationToken.None);
        Assert.Null(saved!.RevokedAt);
        Assert.NotNull(saved.LastFailureAt);
        Assert.Equal("RuStore 403 PERMISSION_DENIED", saved.FailureReason);
    }

    /// <summary>
    /// `adr/0179` §3 / `push-notifications.md`'s own "The question this design exists to answer": the
    /// server never suppresses a push because the operator looks connected. Proven directly, not
    /// merely asserted in a comment - a real <see cref="IConnectionRegistry"/> is seeded to report the
    /// operator as connected (the exact fact the console's own desktop session would have produced),
    /// and the handler is shown to send the push anyway.
    ///
    /// <para>The reason this test can prove the property without wiring the registry into the handler
    /// at all is the property itself: <see cref="NotifyOperatorDevicesHandler"/> takes no
    /// <see cref="IConnectionRegistry"/>/<see cref="INodeFanoutPublisher"/> dependency, so there is no
    /// presence value anywhere in its call graph to consult, correctly or otherwise. This test seeds a
    /// registry the handler is never given, confirms independently (via <see cref="IConnectionRegistry.GetConnectionsAsync"/>)
    /// that it really does report "connected," and then shows the push firing regardless - the
    /// architectural absence made concrete rather than left as an unexercised claim.</para>
    /// </summary>
    [Fact]
    public async Task HandleAsync_SendsAPushEvenWhenThePresenceRegistryReportsTheOperatorConnected()
    {
        var connectionRegistry = new FakeConnectionRegistry();
        connectionRegistry.SeedConnected(
            PrincipalKeys.ForOperator(OperatorId),
            new RegisteredConnection(new ConnectionId("desktop-console-connection"), new NodeId("node-a")));

        // Sanity check: the registry genuinely does say connected - the fact a server-side suppression
        // rule would have consulted, were one to exist.
        var liveConnections = await connectionRegistry.GetConnectionsAsync(PrincipalKeys.ForOperator(OperatorId), CancellationToken.None);
        Assert.NotEmpty(liveConnections);

        var (handler, devices, _, pushSender) = CreateHandler();
        devices.Seed(RegisterDevice(OperatorId, "install-1", "token-1"));

        var result = await handler.HandleAssignmentAsync(
            new NotifyOperatorDeviceForAssignment(new ConversationId(Guid.NewGuid()), VisitorId, OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(pushSender.Calls); // fired anyway - the registry above was never consulted
    }

    private static OperatorDevice RegisterDevice(OperatorId operatorId, string installationId, string token) =>
        OperatorDevice.Register(
            new OperatorDeviceId(Guid.NewGuid()), SiteId, operatorId, installationId, PushProvider.RuStore, "android", token, Now);

    private static (NotifyOperatorDevicesHandler Handler, FakeOperatorDeviceRepository Devices, FakeConversationRepository Conversations, FakePushSender PushSender)
        CreateHandler()
    {
        var devices = new FakeOperatorDeviceRepository();
        var conversations = new FakeConversationRepository();
        var pushSender = new FakePushSender();
        var handler = new NotifyOperatorDevicesHandler(devices, conversations, pushSender, new FakeClock(Now));
        return (handler, devices, conversations, pushSender);
    }

    private static (NotifyOperatorDevicesHandler Handler, FakeOperatorDeviceRepository Devices, FakeConversationRepository Conversations, FakePushSender PushSender)
        CreateHandlerWithOutcome(PushSendOutcome outcome)
    {
        var devices = new FakeOperatorDeviceRepository();
        var conversations = new FakeConversationRepository();
        var pushSender = new FakePushSender(outcome);
        var handler = new NotifyOperatorDevicesHandler(devices, conversations, pushSender, new FakeClock(Now));
        return (handler, devices, conversations, pushSender);
    }

    private static PushMetricsListener ListenToPushMetrics() => new();

    private static void AssertSuppressedReason(IReadOnlyList<Metric> metrics, string reason)
    {
        var suppressed = metrics.Single(m => m.Name == ChatMetrics.PushSuppressedInstrumentName);
        var matched = false;
        foreach (ref readonly var point in suppressed.GetMetricPoints())
        {
            foreach (var tag in point.Tags)
            {
                if (tag.Key == "reason" && (string?)tag.Value == reason)
                {
                    matched = true;
                }
            }
        }

        Assert.True(matched, $"Expected a '{reason}' point on {ChatMetrics.PushSuppressedInstrumentName}.");
    }

    /// <summary>The identical `Sdk.CreateMeterProviderBuilder().AddInMemoryExporter` shape
    /// `ResolveMessageDeliveryTargetsHandlerTests` already uses for <see cref="ChatMetrics"/> - wrapped
    /// here only because this file needs it from more than one test.</summary>
    private sealed class PushMetricsListener : IDisposable
    {
        private readonly MeterProvider _provider;
        private readonly List<Metric> _metrics = [];

        public PushMetricsListener()
        {
            _provider = Sdk.CreateMeterProviderBuilder()
                .AddMeter(ChatMetrics.MeterName)
                .AddInMemoryExporter(_metrics)
                .Build();
        }

        public IReadOnlyList<Metric> Metrics => _metrics;

        public void ForceFlush() => _provider.ForceFlush();

        public void Dispose() => _provider.Dispose();
    }
}
