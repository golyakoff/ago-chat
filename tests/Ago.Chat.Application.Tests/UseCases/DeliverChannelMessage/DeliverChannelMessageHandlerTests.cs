using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.DeliverChannelMessage;

/// <summary>
/// `14-02`: the outbound half of `14-01`'s port, proven for the first time - see
/// <c>Application.UseCases.DeliverChannelMessage.DeliverChannelMessageHandler</c>'s own remarks for the
/// loop guard and idempotency reasoning these tests make falsifiable.
/// </summary>
public class DeliverChannelMessageHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());

    private sealed record Harness(
        Application.UseCases.DeliverChannelMessage.DeliverChannelMessageHandler Handler,
        FakeConversationRepository Conversations,
        FakeChannelIdentityRepository Identities,
        FakeVisitorRepository Visitors,
        FakeModuleTaskChannelPreferenceRepository ModuleTaskPreferences,
        FakeInboundChannelAdapterRegistry Adapters,
        FakeChannelDeliveryRepository Deliveries);

    private static Harness CreateHarness(out Conversation conversation, out FakeInboundChannelAdapter maxAdapter)
    {
        var conversations = new FakeConversationRepository();
        var identities = new FakeChannelIdentityRepository();
        var visitors = new FakeVisitorRepository();
        var moduleTaskPreferences = new FakeModuleTaskChannelPreferenceRepository();
        var adapters = new FakeInboundChannelAdapterRegistry();
        var deliveries = new FakeChannelDeliveryRepository();
        maxAdapter = new FakeInboundChannelAdapter(ChannelKind.Max);
        adapters.Register(maxAdapter);

        var visitorId = new VisitorId(Guid.NewGuid());
        conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, visitorId, Now);
        conversation.AssignTo(OperatorId, Now);
        conversations.Seed(conversation);
        visitors.Seed(new Visitor(visitorId, SiteId, Now));

        var handler = new Application.UseCases.DeliverChannelMessage.DeliverChannelMessageHandler(
            conversations, identities, visitors, moduleTaskPreferences, adapters,
            deliveries, new FakeIdGenerator(), new FakeClock(Now));

        return new Harness(handler, conversations, identities, visitors, moduleTaskPreferences, adapters, deliveries);
    }

    private static Task LinkMaxIdentity(FakeChannelIdentityRepository identities, VisitorId visitorId, string address = "555000") =>
        identities.SaveAsync(
            ChannelIdentity.Link(new ChannelIdentityId(Guid.NewGuid()), SiteId, ChannelKind.Max, new ExternalChannelAddress(address), visitorId, Now),
            CancellationToken.None);

    [Fact]
    public async Task HandleAsync_ForAnOperatorMessageOnALinkedConversation_RelaysItThroughTheAdapter()
    {
        var harness = CreateHarness(out var conversation, out var maxAdapter);
        await LinkMaxIdentity(harness.Identities, conversation.VisitorId);
        var message = conversation.AddOperatorMessage(OperatorId, new MessageId(Guid.NewGuid()), new MessageBody("hi there"), Now);

        var outcome = await harness.Handler.HandleAsync(
            new Application.UseCases.DeliverChannelMessage.DeliverChannelMessage(
                SiteId, conversation.Id, message.Id, MessageAuthorKind.Operator, message.Sequence),
            CancellationToken.None);

        Assert.Equal(Application.UseCases.DeliverChannelMessage.DeliverChannelMessageOutcome.Delivered, outcome);
        var sent = Assert.Single(maxAdapter.Sent);
        Assert.Equal("hi there", sent.Body.Value);
        Assert.Equal(message.Id, sent.MessageId);

        // `23-19`: the fact §9 says was "already in hand and thrown away" is now recorded.
        var delivery = Assert.Single(harness.Deliveries.Saved);
        Assert.Equal(message.Id, delivery.MessageId);
        Assert.Equal(SiteId, delivery.SiteId);
        Assert.Equal(conversation.Id, delivery.ConversationId);
        Assert.Equal(ChannelKind.Max, delivery.ChannelKind);
        Assert.Equal(ChannelDeliveryStatus.Delivered, delivery.Status);
        Assert.Equal("fake-1", delivery.ProviderMessageId);
        Assert.Null(delivery.FailureReason);
    }

    /// <summary>`23-19`'s own Done-when: a redelivered broker message must not grow a second row - one
    /// triggering operator message is one outbound send, and <see cref="ChannelDelivery.MessageId"/> is
    /// this table's own idempotency key.</summary>
    [Fact]
    public async Task HandleAsync_CalledTwiceForTheSameTriggerMessage_CollapsesOntoOneDeliveryRow()
    {
        var harness = CreateHarness(out var conversation, out _);
        await LinkMaxIdentity(harness.Identities, conversation.VisitorId);
        var message = conversation.AddOperatorMessage(OperatorId, new MessageId(Guid.NewGuid()), new MessageBody("hi there"), Now);
        var command = new Application.UseCases.DeliverChannelMessage.DeliverChannelMessage(
            SiteId, conversation.Id, message.Id, MessageAuthorKind.Operator, message.Sequence);

        var firstOutcome = await harness.Handler.HandleAsync(command, CancellationToken.None);
        var secondOutcome = await harness.Handler.HandleAsync(command, CancellationToken.None);

        Assert.Equal(Application.UseCases.DeliverChannelMessage.DeliverChannelMessageOutcome.Delivered, firstOutcome);
        Assert.Equal(Application.UseCases.DeliverChannelMessage.DeliverChannelMessageOutcome.Delivered, secondOutcome);
        Assert.Single(harness.Deliveries.Saved);
    }

    /// <summary>
    /// `14-13`/`adr/0079` decision 5, case 1 of 3: a live preference wins over the "most recently seen"
    /// rule, even when the most-recently-seen identity is a different, also-active channel - proving
    /// the preference is actually consulted first, not merely tolerated when it happens to agree.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenThePreferredIdentityIsStillActive_UsesItOverTheMoreRecentlySeenOne()
    {
        var harness = CreateHarness(out var conversation, out var maxAdapter);
        var telegramAdapter = new FakeInboundChannelAdapter(ChannelKind.Telegram);
        harness.Adapters.Register(telegramAdapter);

        var maxIdentity = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), SiteId, ChannelKind.Max, new ExternalChannelAddress("555000"),
            conversation.VisitorId, Now);
        await harness.Identities.SaveAsync(maxIdentity, CancellationToken.None);
        // Linked an hour later - the most-recently-seen rule alone would pick this one instead.
        var telegramIdentity = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), SiteId, ChannelKind.Telegram, new ExternalChannelAddress("tg-user-1"),
            conversation.VisitorId, Now.AddHours(1));
        await harness.Identities.SaveAsync(telegramIdentity, CancellationToken.None);

        var visitor = await harness.Visitors.GetByIdAsync(conversation.VisitorId, CancellationToken.None);
        visitor!.SetPreferredChannelIdentity(maxIdentity.Id);
        harness.Visitors.Seed(visitor);

        var message = conversation.AddOperatorMessage(OperatorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        var outcome = await harness.Handler.HandleAsync(
            new Application.UseCases.DeliverChannelMessage.DeliverChannelMessage(
                SiteId, conversation.Id, message.Id, MessageAuthorKind.Operator, message.Sequence),
            CancellationToken.None);

        Assert.Equal(Application.UseCases.DeliverChannelMessage.DeliverChannelMessageOutcome.Delivered, outcome);
        Assert.Single(maxAdapter.Sent);
        Assert.Empty(telegramAdapter.Sent);
    }

    /// <summary>
    /// `14-13`/`adr/0079` decision 5, case 2 of 3: the preferred identity was unlinked after the
    /// preference was set - delivery must fall back to the unchanged most-recent rule, not throw and not
    /// silently deliver nowhere. Proves the read-time-tolerance design choice (this handler's own
    /// remarks) actually works, since nothing here ever clears <see cref="Visitor.PreferredChannelIdentityId"/>
    /// on unlink.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenThePreferredIdentityHasSinceBeenUnlinked_FallsBackToTheMostRecentActiveOne()
    {
        var harness = CreateHarness(out var conversation, out var maxAdapter);
        var telegramAdapter = new FakeInboundChannelAdapter(ChannelKind.Telegram);
        harness.Adapters.Register(telegramAdapter);

        var maxIdentity = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), SiteId, ChannelKind.Max, new ExternalChannelAddress("555000"),
            conversation.VisitorId, Now);
        await harness.Identities.SaveAsync(maxIdentity, CancellationToken.None);
        var telegramIdentity = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), SiteId, ChannelKind.Telegram, new ExternalChannelAddress("tg-user-1"),
            conversation.VisitorId, Now.AddHours(1));
        await harness.Identities.SaveAsync(telegramIdentity, CancellationToken.None);

        var visitor = await harness.Visitors.GetByIdAsync(conversation.VisitorId, CancellationToken.None);
        visitor!.SetPreferredChannelIdentity(maxIdentity.Id);
        harness.Visitors.Seed(visitor);

        // Unlinked after the preference was set - the visitor's own PreferredChannelIdentityId is left
        // pointing at it, deliberately (this handler's own remarks on why read-time tolerance was
        // chosen over a write-time cleanup).
        maxIdentity.Unlink(Now.AddMinutes(30));
        await harness.Identities.SaveAsync(maxIdentity, CancellationToken.None);

        var message = conversation.AddOperatorMessage(OperatorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        var outcome = await harness.Handler.HandleAsync(
            new Application.UseCases.DeliverChannelMessage.DeliverChannelMessage(
                SiteId, conversation.Id, message.Id, MessageAuthorKind.Operator, message.Sequence),
            CancellationToken.None);

        Assert.Equal(Application.UseCases.DeliverChannelMessage.DeliverChannelMessageOutcome.Delivered, outcome);
        Assert.Empty(maxAdapter.Sent);
        Assert.Single(telegramAdapter.Sent);
    }

    /// <summary>
    /// `14-13`/`adr/0079` decision 5, case 3 of 3: no preference was ever set - the unchanged
    /// most-recent rule alone decides, exactly as it did before this item, even with more than one
    /// active identity to choose between.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenNoPreferenceIsSet_UsesTheMostRecentlySeenIdentity()
    {
        var harness = CreateHarness(out var conversation, out var maxAdapter);
        var telegramAdapter = new FakeInboundChannelAdapter(ChannelKind.Telegram);
        harness.Adapters.Register(telegramAdapter);

        var maxIdentity = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), SiteId, ChannelKind.Max, new ExternalChannelAddress("555000"),
            conversation.VisitorId, Now);
        await harness.Identities.SaveAsync(maxIdentity, CancellationToken.None);
        var telegramIdentity = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), SiteId, ChannelKind.Telegram, new ExternalChannelAddress("tg-user-1"),
            conversation.VisitorId, Now.AddHours(1));
        await harness.Identities.SaveAsync(telegramIdentity, CancellationToken.None);
        // No preference set - CreateHarness's own seeded visitor already has PreferredChannelIdentityId
        // null, the same starting state every visitor has before an operator ever sets one.

        var message = conversation.AddOperatorMessage(OperatorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        var outcome = await harness.Handler.HandleAsync(
            new Application.UseCases.DeliverChannelMessage.DeliverChannelMessage(
                SiteId, conversation.Id, message.Id, MessageAuthorKind.Operator, message.Sequence),
            CancellationToken.None);

        Assert.Equal(Application.UseCases.DeliverChannelMessage.DeliverChannelMessageOutcome.Delivered, outcome);
        Assert.Empty(maxAdapter.Sent);
        Assert.Single(telegramAdapter.Sent);
    }

    /// <summary>
    /// `20-11`: case 1 of 3 of the widened resolution order - this conversation's own active booking has
    /// a priority list set, and it must win over both `14-13`'s own preference and the unchanged
    /// most-recent rule, even when both of those would have picked a different, also-active identity.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenTheActiveBookingHasAUsablePriorityList_UsesItOverThePreferenceAndMostRecent()
    {
        var harness = CreateHarness(out var conversation, out var maxAdapter);
        var telegramAdapter = new FakeInboundChannelAdapter(ChannelKind.Telegram);
        var vkAdapter = new FakeInboundChannelAdapter(ChannelKind.Vk);
        harness.Adapters.Register(telegramAdapter);
        harness.Adapters.Register(vkAdapter);

        var maxIdentity = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), SiteId, ChannelKind.Max, new ExternalChannelAddress("555000"),
            conversation.VisitorId, Now);
        await harness.Identities.SaveAsync(maxIdentity, CancellationToken.None);
        // Most recently seen - the unchanged most-recent rule alone would pick this one.
        var telegramIdentity = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), SiteId, ChannelKind.Telegram, new ExternalChannelAddress("tg-user-1"),
            conversation.VisitorId, Now.AddHours(1));
        await harness.Identities.SaveAsync(telegramIdentity, CancellationToken.None);
        var vkIdentity = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), SiteId, ChannelKind.Vk, new ExternalChannelAddress("vk-user-1"),
            conversation.VisitorId, Now.AddHours(2));
        await harness.Identities.SaveAsync(vkIdentity, CancellationToken.None);

        // 14-13's own preference - would win the old two-step resolution, but must lose to this item's
        // own list below.
        var visitor = await harness.Visitors.GetByIdAsync(conversation.VisitorId, CancellationToken.None);
        visitor!.SetPreferredChannelIdentity(telegramIdentity.Id);
        harness.Visitors.Seed(visitor);

        var task = conversation.StartModuleTask(
            new ModuleTaskId(Guid.NewGuid()), new ModuleKey("booking-flow"), "ext-1", Now, null, null, []);
        harness.ModuleTaskPreferences.Seed(ModuleTaskChannelPreference.Add(
            new ModuleTaskChannelPreferenceId(Guid.NewGuid()), SiteId, task.Id, conversation.VisitorId, vkIdentity.Id,
            priority: 1, Now));
        harness.Conversations.Seed(conversation);

        var message = conversation.AddOperatorMessage(OperatorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        var outcome = await harness.Handler.HandleAsync(
            new Application.UseCases.DeliverChannelMessage.DeliverChannelMessage(
                SiteId, conversation.Id, message.Id, MessageAuthorKind.Operator, message.Sequence),
            CancellationToken.None);

        Assert.Equal(Application.UseCases.DeliverChannelMessage.DeliverChannelMessageOutcome.Delivered, outcome);
        Assert.Single(vkAdapter.Sent);
        Assert.Empty(telegramAdapter.Sent);
        Assert.Empty(maxAdapter.Sent);
    }

    /// <summary>
    /// `20-11`: the priority list's own internal ordering - the top-ranked entry was unlinked after the
    /// list was set, so resolution must move to the next-ranked entry in the same list rather than
    /// abandoning the list entirely for `14-13`'s preference.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenTheTopPriorityEntryIsUnlinked_FallsToTheNextEntryInTheSameList()
    {
        var harness = CreateHarness(out var conversation, out var maxAdapter);
        var telegramAdapter = new FakeInboundChannelAdapter(ChannelKind.Telegram);
        var vkAdapter = new FakeInboundChannelAdapter(ChannelKind.Vk);
        harness.Adapters.Register(telegramAdapter);
        harness.Adapters.Register(vkAdapter);

        var vkIdentity = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), SiteId, ChannelKind.Vk, new ExternalChannelAddress("vk-user-1"), conversation.VisitorId, Now);
        vkIdentity.Unlink(Now.AddMinutes(1));
        await harness.Identities.SaveAsync(vkIdentity, CancellationToken.None);
        var telegramIdentity = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), SiteId, ChannelKind.Telegram, new ExternalChannelAddress("tg-user-1"), conversation.VisitorId, Now);
        await harness.Identities.SaveAsync(telegramIdentity, CancellationToken.None);
        // Most recently seen of the three, and not in the priority list at all - what the old
        // (pre-20-11) resolution order would pick once the unlinked top entry is out of the way,
        // proving this test actually discriminates "moved to the next entry in the same list" from
        // "fell all the way through to the unchanged most-recent rule and got lucky."
        var maxIdentity = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), SiteId, ChannelKind.Max, new ExternalChannelAddress("555000"), conversation.VisitorId, Now.AddHours(1));
        await harness.Identities.SaveAsync(maxIdentity, CancellationToken.None);

        var task = conversation.StartModuleTask(
            new ModuleTaskId(Guid.NewGuid()), new ModuleKey("booking-flow"), "ext-1", Now, null, null, []);
        harness.ModuleTaskPreferences.Seed(ModuleTaskChannelPreference.Add(
            new ModuleTaskChannelPreferenceId(Guid.NewGuid()), SiteId, task.Id, conversation.VisitorId, vkIdentity.Id, priority: 1, Now));
        harness.ModuleTaskPreferences.Seed(ModuleTaskChannelPreference.Add(
            new ModuleTaskChannelPreferenceId(Guid.NewGuid()), SiteId, task.Id, conversation.VisitorId, telegramIdentity.Id, priority: 2, Now));
        harness.Conversations.Seed(conversation);

        var message = conversation.AddOperatorMessage(OperatorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        var outcome = await harness.Handler.HandleAsync(
            new Application.UseCases.DeliverChannelMessage.DeliverChannelMessage(
                SiteId, conversation.Id, message.Id, MessageAuthorKind.Operator, message.Sequence),
            CancellationToken.None);

        Assert.Equal(Application.UseCases.DeliverChannelMessage.DeliverChannelMessageOutcome.Delivered, outcome);
        Assert.Single(telegramAdapter.Sent);
        Assert.Empty(vkAdapter.Sent);
        Assert.Empty(maxAdapter.Sent);
    }

    /// <summary>
    /// `20-11`: case 2 and 3 unaffected regression guard, this item's own axis - a conversation with no
    /// active module task at all has nothing for the new resolution step to find, so control passes
    /// straight through to `14-13`'s preference exactly as it did before this item existed.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WithNoActiveModuleTask_TheBookingPriorityStepContributesNothing_FallsBackToThePreference()
    {
        var harness = CreateHarness(out var conversation, out var maxAdapter);
        var telegramAdapter = new FakeInboundChannelAdapter(ChannelKind.Telegram);
        harness.Adapters.Register(telegramAdapter);

        var maxIdentity = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), SiteId, ChannelKind.Max, new ExternalChannelAddress("555000"), conversation.VisitorId, Now);
        await harness.Identities.SaveAsync(maxIdentity, CancellationToken.None);
        var telegramIdentity = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), SiteId, ChannelKind.Telegram, new ExternalChannelAddress("tg-user-1"), conversation.VisitorId, Now.AddHours(1));
        await harness.Identities.SaveAsync(telegramIdentity, CancellationToken.None);

        var visitor = await harness.Visitors.GetByIdAsync(conversation.VisitorId, CancellationToken.None);
        visitor!.SetPreferredChannelIdentity(maxIdentity.Id);
        harness.Visitors.Seed(visitor);
        // No StartModuleTask call at all - the conversation has never run any module.

        var message = conversation.AddOperatorMessage(OperatorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        var outcome = await harness.Handler.HandleAsync(
            new Application.UseCases.DeliverChannelMessage.DeliverChannelMessage(
                SiteId, conversation.Id, message.Id, MessageAuthorKind.Operator, message.Sequence),
            CancellationToken.None);

        Assert.Equal(Application.UseCases.DeliverChannelMessage.DeliverChannelMessageOutcome.Delivered, outcome);
        Assert.Single(maxAdapter.Sent);
        Assert.Empty(telegramAdapter.Sent);
    }

    /// <summary>The loop guard: a visitor message (what an inbound MAX message itself is authored as)
    /// must never be relayed back out, or every inbound MAX message would echo straight back to the
    /// same chat.</summary>
    [Fact]
    public async Task HandleAsync_ForAVisitorMessage_DoesNotRelayIt()
    {
        var harness = CreateHarness(out var conversation, out var maxAdapter);
        await LinkMaxIdentity(harness.Identities, conversation.VisitorId);

        var outcome = await harness.Handler.HandleAsync(
            new Application.UseCases.DeliverChannelMessage.DeliverChannelMessage(
                SiteId, conversation.Id, new MessageId(Guid.NewGuid()), MessageAuthorKind.Visitor, 1),
            CancellationToken.None);

        Assert.Equal(Application.UseCases.DeliverChannelMessage.DeliverChannelMessageOutcome.NotAnOperatorMessage, outcome);
        Assert.Empty(maxAdapter.Sent);
        Assert.Empty(harness.Deliveries.Saved);
    }

    /// <summary>Out of this item's scope (14-03's own job) - see the handler's own remarks.</summary>
    [Fact]
    public async Task HandleAsync_ForASystemMessage_DoesNotRelayIt()
    {
        var harness = CreateHarness(out var conversation, out var maxAdapter);
        await LinkMaxIdentity(harness.Identities, conversation.VisitorId);

        var outcome = await harness.Handler.HandleAsync(
            new Application.UseCases.DeliverChannelMessage.DeliverChannelMessage(
                SiteId, conversation.Id, new MessageId(Guid.NewGuid()), MessageAuthorKind.System, 1),
            CancellationToken.None);

        Assert.Equal(Application.UseCases.DeliverChannelMessage.DeliverChannelMessageOutcome.NotAnOperatorMessage, outcome);
        Assert.Empty(maxAdapter.Sent);
        Assert.Empty(harness.Deliveries.Saved);
    }

    /// <summary>`23-19`'s own Done-when: "a conversation with no linked channel writes nothing at all -
    /// the no-linked-channel outcome is not a delivery failure and must not be reported as one."</summary>
    [Fact]
    public async Task HandleAsync_WhenTheVisitorHasNoLinkedChannel_DoesNotRelayIt()
    {
        var harness = CreateHarness(out var conversation, out var maxAdapter);
        var message = conversation.AddOperatorMessage(OperatorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);

        var outcome = await harness.Handler.HandleAsync(
            new Application.UseCases.DeliverChannelMessage.DeliverChannelMessage(
                SiteId, conversation.Id, message.Id, MessageAuthorKind.Operator, message.Sequence),
            CancellationToken.None);

        Assert.Equal(Application.UseCases.DeliverChannelMessage.DeliverChannelMessageOutcome.NoLinkedChannel, outcome);
        Assert.Empty(maxAdapter.Sent);
        Assert.Empty(harness.Deliveries.Saved);
    }

    [Fact]
    public async Task HandleAsync_WhenNoAdapterIsRegisteredForTheLinkedChannel_ReturnsNoAdapterRegistered()
    {
        var conversations = new FakeConversationRepository();
        var identities = new FakeChannelIdentityRepository();
        var visitors = new FakeVisitorRepository();
        var adapters = new FakeInboundChannelAdapterRegistry(); // nothing registered

        var visitorId = new VisitorId(Guid.NewGuid());
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, visitorId, Now);
        conversation.AssignTo(OperatorId, Now);
        conversations.Seed(conversation);
        await LinkMaxIdentity(identities, visitorId);
        var message = conversation.AddOperatorMessage(OperatorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);

        var deliveries = new FakeChannelDeliveryRepository();
        var handler = new Application.UseCases.DeliverChannelMessage.DeliverChannelMessageHandler(
            conversations, identities, visitors, new FakeModuleTaskChannelPreferenceRepository(), adapters,
            deliveries, new FakeIdGenerator(), new FakeClock(Now));
        var outcome = await handler.HandleAsync(
            new Application.UseCases.DeliverChannelMessage.DeliverChannelMessage(
                SiteId, conversation.Id, message.Id, MessageAuthorKind.Operator, message.Sequence),
            CancellationToken.None);

        Assert.Equal(Application.UseCases.DeliverChannelMessage.DeliverChannelMessageOutcome.NoAdapterRegistered, outcome);
        // Nothing was attempted - this item's own Done-when: only Delivered/Refused ever write a row.
        Assert.Empty(deliveries.Saved);
    }

    /// <summary>A provider's terminal refusal (`Domain.ChannelSendOutcome.Refused`, e.g. a revoked bot
    /// token) is a value, not an exception - this handler must map it through, never swallow it or
    /// throw.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheAdapterRefusesTheMessage_ReturnsRefused()
    {
        var harness = CreateHarness(out var conversation, out var maxAdapter);
        await LinkMaxIdentity(harness.Identities, conversation.VisitorId);
        maxAdapter.RefuseWith = "no active bot";
        var message = conversation.AddOperatorMessage(OperatorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);

        var outcome = await harness.Handler.HandleAsync(
            new Application.UseCases.DeliverChannelMessage.DeliverChannelMessage(
                SiteId, conversation.Id, message.Id, MessageAuthorKind.Operator, message.Sequence),
            CancellationToken.None);

        Assert.Equal(Application.UseCases.DeliverChannelMessage.DeliverChannelMessageOutcome.Refused, outcome);

        // `23-19`'s own Done-when: a refused send writes a row saying refused, with the provider's own
        // detail - failure is a recorded outcome, not an absence.
        var delivery = Assert.Single(harness.Deliveries.Saved);
        Assert.Equal(ChannelDeliveryStatus.Refused, delivery.Status);
        Assert.Equal("no active bot", delivery.FailureReason);
        Assert.Null(delivery.ProviderMessageId);
    }

    /// <summary>A transient fault (thrown, per `IInboundChannelAdapter.SendAsync`'s own contract) must
    /// propagate, not be swallowed - it is what lets messaging.md's retry-then-dead-letter path work at
    /// the consumer above this handler.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheAdapterThrows_PropagatesTheException()
    {
        var harness = CreateHarness(out var conversation, out var maxAdapter);
        await LinkMaxIdentity(harness.Identities, conversation.VisitorId);
        maxAdapter.FailuresBeforeSuccess = 1;
        var message = conversation.AddOperatorMessage(OperatorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Handler.HandleAsync(
            new Application.UseCases.DeliverChannelMessage.DeliverChannelMessage(
                SiteId, conversation.Id, message.Id, MessageAuthorKind.Operator, message.Sequence),
            CancellationToken.None));
    }
}
