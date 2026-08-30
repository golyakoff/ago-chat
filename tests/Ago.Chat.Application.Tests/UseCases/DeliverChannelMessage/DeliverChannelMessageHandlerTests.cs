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
        FakeInboundChannelAdapterRegistry Adapters);

    private static Harness CreateHarness(out Conversation conversation, out FakeInboundChannelAdapter maxAdapter)
    {
        var conversations = new FakeConversationRepository();
        var identities = new FakeChannelIdentityRepository();
        var visitors = new FakeVisitorRepository();
        var adapters = new FakeInboundChannelAdapterRegistry();
        maxAdapter = new FakeInboundChannelAdapter(ChannelKind.Max);
        adapters.Register(maxAdapter);

        var visitorId = new VisitorId(Guid.NewGuid());
        conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, visitorId, Now);
        conversation.AssignTo(OperatorId, Now);
        conversations.Seed(conversation);
        visitors.Seed(new Visitor(visitorId, SiteId, Now));

        var handler = new Application.UseCases.DeliverChannelMessage.DeliverChannelMessageHandler(
            conversations, identities, visitors, adapters);

        return new Harness(handler, conversations, identities, visitors, adapters);
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
    }

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

        var handler = new Application.UseCases.DeliverChannelMessage.DeliverChannelMessageHandler(
            conversations, identities, visitors, adapters);
        var outcome = await handler.HandleAsync(
            new Application.UseCases.DeliverChannelMessage.DeliverChannelMessage(
                SiteId, conversation.Id, message.Id, MessageAuthorKind.Operator, message.Sequence),
            CancellationToken.None);

        Assert.Equal(Application.UseCases.DeliverChannelMessage.DeliverChannelMessageOutcome.NoAdapterRegistered, outcome);
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
