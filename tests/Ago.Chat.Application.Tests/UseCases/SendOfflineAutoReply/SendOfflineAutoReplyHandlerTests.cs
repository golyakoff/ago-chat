using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetSiteConfigById;
using Ago.Chat.Application.UseCases.SendOfflineAutoReply;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.SendOfflineAutoReply;

/// <summary>
/// `14-04`'s Done-when, at the level the item names it: "a message arriving with no operator online
/// and the flag enabled triggers the scripted reply; the same message with the flag disabled does
/// not."
///
/// <para>Three further things are proven here that the item does not spell out but that the design
/// rests on: that an auto-reply cannot trigger an auto-reply
/// (<see cref="AnAutoReplysOwnMessageAcceptedProducesNoSecondReply"/> - remove the guard and this test
/// is what fails), that an operator being merely *busy* is not the same as being absent, and that a
/// redelivered <c>MessageAccepted</c> produces no second reply.</para>
/// </summary>
public class SendOfflineAutoReplyHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private const string PublicKey = "shop_7f3a";

    private sealed record Fixture(
        SendOfflineAutoReplyHandler Handler,
        Conversation Conversation,
        FakeOutboxWriter Outbox,
        FakeInboxChecker Inbox);

    private static OfflineAutoReplySettings Script(bool enabled) =>
        new(enabled, "We are closed - we will reply in the morning.",
            [new OfflineAutoReplyRule("refund", "Refunds take three working days.")]);

    // `onlineOperator`/`offlineOperator` are the two states this decision actually turns on. There is
    // deliberately no "busy operator" case here: the condition ignores capacity entirely (see
    // IOperatorRepository.AnyOnlineForSiteAsync's remarks), so an at-capacity operator is just an
    // Online one and is covered by WhenAnOperatorIsOnline_NoReplyIsSent. Capacity lives in a shadow
    // property this level cannot set anyway - proving the SQL is Ago.Chat.Integration.Tests' job.
    private static Fixture CreateFixture(
        bool enabled = true,
        bool onlineOperator = false,
        bool offlineOperator = false,
        bool assigned = false,
        string visitorText = "hello, anybody there?")
    {
        var site = new Site(SiteId, PublicKey, []);
        site.UpdateOfflineAutoReply(Script(enabled), Now);
        var sites = new FakeSiteRepository();
        sites.Seed(site);

        var operators = new FakeOperatorRepository();
        if (onlineOperator)
        {
            operators.Seed(new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Online, 5));
        }

        if (offlineOperator)
        {
            operators.Seed(new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, 5));
        }

        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversation.AddVisitorMessage(
            VisitorId, new MessageId(Guid.NewGuid()), new MessageBody(visitorText), Now);
        if (assigned)
        {
            conversation.AssignTo(new OperatorId(Guid.NewGuid()), Now);
        }

        conversation.ClearDomainEvents();

        var conversations = new FakeConversationRepository();
        conversations.Seed(conversation);

        var outbox = new FakeOutboxWriter();
        var inbox = new FakeInboxChecker();
        var handler = new SendOfflineAutoReplyHandler(
            new GetSiteConfigByIdHandler(sites, new FakeCache()),
            conversations, operators, outbox, inbox, new FakeClock(Now), new FakeIdGenerator());

        return new Fixture(handler, conversation, outbox, inbox);
    }

    private static Ago.Chat.Application.UseCases.SendOfflineAutoReply.SendOfflineAutoReply Trigger(
        Conversation conversation,
        MessageAuthorKind authorKind = MessageAuthorKind.Visitor,
        int? sequence = null,
        Guid? messageId = null) =>
        new(messageId ?? Guid.NewGuid(), SiteId, conversation.Id, authorKind,
            sequence ?? conversation.LastSequence);

    [Fact]
    public async Task WithTheFlagEnabledAndNobodyOnline_SendsTheScriptedReply()
    {
        var fixture = CreateFixture(enabled: true);

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(OfflineAutoReplyOutcome.Sent, result.Value);

        var reply = fixture.Conversation.Messages.Last();
        Assert.Equal(MessageAuthorKind.System, reply.AuthorKind);
        Assert.Equal("We are closed - we will reply in the morning.", reply.Body.Value);
    }

    [Fact]
    public async Task WithTheFlagDisabled_TheSameMessageProducesNoReply()
    {
        var fixture = CreateFixture(enabled: false);
        var messagesBefore = fixture.Conversation.Messages.Count;

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(OfflineAutoReplyOutcome.Disabled, result.Value);
        Assert.Equal(messagesBefore, fixture.Conversation.Messages.Count);
        Assert.Empty(fixture.Outbox.Enqueued);
    }

    [Fact]
    public async Task WhenAKeywordMatches_ThatRulesReplyIsSentInsteadOfTheFallback()
    {
        var fixture = CreateFixture(visitorText: "how long does a REFUND take?");

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(OfflineAutoReplyOutcome.Sent, result.Value);
        Assert.Equal("Refunds take three working days.", fixture.Conversation.Messages.Last().Body.Value);
    }

    /// <summary>
    /// <b>The loop guard.</b> This is the same <c>MessageAccepted</c> the reply this handler just wrote
    /// will itself publish - fed straight back in. Nothing happens, and nothing can: the reply is
    /// authored <see cref="MessageAuthorKind.System"/> and this handler acts on
    /// <see cref="MessageAuthorKind.Visitor"/> alone.
    /// </summary>
    [Fact]
    public async Task AnAutoReplysOwnMessageAcceptedProducesNoSecondReply()
    {
        var fixture = CreateFixture();

        var first = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);
        Assert.Equal(OfflineAutoReplyOutcome.Sent, first.Value);

        var reply = fixture.Conversation.Messages.Last();
        Assert.Equal(MessageAuthorKind.System, reply.AuthorKind);
        var messagesAfterFirstReply = fixture.Conversation.Messages.Count;

        // Exactly what OfflineAutoReplyConsumer would hand this handler when the reply's own
        // MessageAccepted comes round the topic - same conversation, the reply's own sequence, the
        // reply's own author kind.
        var echo = await fixture.Handler.HandleAsync(
            Trigger(fixture.Conversation, MessageAuthorKind.System, reply.Sequence),
            CancellationToken.None);

        Assert.True(echo.IsSuccess);
        Assert.Equal(OfflineAutoReplyOutcome.NotAVisitorMessage, echo.Value);
        Assert.Equal(messagesAfterFirstReply, fixture.Conversation.Messages.Count);
    }

    /// <summary>The guard's second half, aimed at the database rather than the wire field: even if a
    /// producer mislabelled the event, the persisted message is what decides.</summary>
    [Fact]
    public async Task AMislabelledEventForANonVisitorMessageStillProducesNoReply()
    {
        var fixture = CreateFixture();
        var reply = fixture.Conversation.AddSystemMessage(
            new MessageId(Guid.NewGuid()), new MessageBody("Closed."), Now);
        fixture.Conversation.ClearDomainEvents();
        var messagesBefore = fixture.Conversation.Messages.Count;

        var result = await fixture.Handler.HandleAsync(
            Trigger(fixture.Conversation, MessageAuthorKind.Visitor, reply.Sequence),
            CancellationToken.None);

        Assert.Equal(OfflineAutoReplyOutcome.NotAVisitorMessage, result.Value);
        Assert.Equal(messagesBefore, fixture.Conversation.Messages.Count);
    }

    [Fact]
    public async Task WhenAnOperatorIsOnline_NoReplyIsSent()
    {
        var fixture = CreateFixture(onlineOperator: true);

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(OfflineAutoReplyOutcome.OperatorOnline, result.Value);
        Assert.Empty(fixture.Outbox.Enqueued);
    }

    [Fact]
    public async Task WhenTheOnlyOperatorIsOffline_TheReplyIsStillSent()
    {
        // An operators row that exists but is Offline is exactly the shop-is-closed case, and must not
        // be mistaken for presence.
        var fixture = CreateFixture(offlineOperator: true);

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(OfflineAutoReplyOutcome.Sent, result.Value);
    }

    [Fact]
    public async Task WhenTheConversationIsAlreadyAssigned_NoReplyIsSent()
    {
        var fixture = CreateFixture(assigned: true);

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(OfflineAutoReplyOutcome.ConversationNotWaiting, result.Value);
        Assert.Empty(fixture.Outbox.Enqueued);
    }

    /// <summary>`CLAUDE.md` rule 5. The inbox ledger is the guarantee (`adr/0017`); this proves the
    /// handler asks it and honours its answer. That the losing save also discards the staged reply is
    /// a property of the real <c>EfInboxChecker</c>'s transaction, which no in-memory fake can show -
    /// <c>FakeInboxChecker</c>'s own remarks say so, and it is stated in this item's report as
    /// unverified at this level.</summary>
    [Fact]
    public async Task ARedeliveredTriggerProducesNoSecondReply()
    {
        var fixture = CreateFixture();
        var messageId = Guid.NewGuid();

        var first = await fixture.Handler.HandleAsync(
            Trigger(fixture.Conversation, messageId: messageId), CancellationToken.None);
        Assert.Equal(OfflineAutoReplyOutcome.Sent, first.Value);

        var redelivery = await fixture.Handler.HandleAsync(
            Trigger(fixture.Conversation, sequence: 1, messageId: messageId), CancellationToken.None);

        Assert.True(redelivery.IsSuccess);
        Assert.Equal(OfflineAutoReplyOutcome.AlreadyReplied, redelivery.Value);
    }

    [Fact]
    public async Task ASentReply_IsOutboxedAsMessageAccepted_AndLeavesNoDomainEventsBehind()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        var envelope = Assert.Single(fixture.Outbox.Enqueued);
        Assert.Equal(nameof(MessageAccepted), envelope.Type);
        // Cleared, so a later save of this same aggregate cannot re-enqueue it - every other write
        // path in this codebase does the same.
        Assert.Empty(fixture.Conversation.DomainEvents);
    }
}
