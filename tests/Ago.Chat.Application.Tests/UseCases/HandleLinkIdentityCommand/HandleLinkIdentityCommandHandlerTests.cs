using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Application.UseCases.HandleLinkIdentityCommand;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.HandleLinkIdentityCommand;

/// <summary>
/// `14-12`/`adr/0079`: the visitor-initiated half - a `MessageAccepted`-driven handler, the same shape
/// `RouteConversationToModuleHandlerTests`/`SendOfflineAutoReplyHandlerTests` already exercise their own
/// handlers with.
/// </summary>
public class HandleLinkIdentityCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());

    private sealed record Fixture(
        Application.UseCases.HandleLinkIdentityCommand.HandleLinkIdentityCommandHandler Handler,
        Conversation Conversation, FakePendingChannelLinkRequestRepository PendingLinks,
        FakeOutboxWriter Outbox, FakeInboxChecker Inbox);

    private static Fixture CreateFixture(Action<Conversation>? arrange = null, FakeInboxChecker? inbox = null)
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        arrange?.Invoke(conversation);
        conversation.ClearDomainEvents();

        var conversations = new FakeConversationRepository();
        conversations.Seed(conversation);

        var pendingLinks = new FakePendingChannelLinkRequestRepository();
        var outbox = new FakeOutboxWriter();
        inbox ??= new FakeInboxChecker();

        var handler = new Application.UseCases.HandleLinkIdentityCommand.HandleLinkIdentityCommandHandler(
            conversations, pendingLinks, new FakePendingChannelLinkCodeGenerator("482913"),
            new PendingChannelLinkRequestOptions { ValidFor = TimeSpan.FromMinutes(15) }, outbox, inbox,
            new FakeClock(Now), new FakeIdGenerator());

        return new Fixture(handler, conversation, pendingLinks, outbox, inbox);
    }

    private static Application.UseCases.HandleLinkIdentityCommand.HandleLinkIdentityCommand Trigger(
        Conversation conversation, MessageAuthorKind authorKind = MessageAuthorKind.Visitor, int? sequence = null,
        Guid? messageId = null) =>
        new(messageId ?? Guid.NewGuid(), SiteId, conversation.Id, authorKind, sequence ?? conversation.LastSequence);

    [Fact]
    public async Task HandleAsync_WithARealCommand_CreatesALivePendingRequest_AndRepliesWithTheCode()
    {
        var fixture = CreateFixture();
        fixture.Conversation.AddVisitorMessage(
            VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("/linkidentity telegram"), Now);

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(LinkIdentityCommandOutcome.RequestCreated, result.Value);
        var request = Assert.Single(fixture.PendingLinks.All);
        Assert.Equal(VisitorId, request.VisitorId);
        Assert.Equal(SiteId, request.SiteId);
        Assert.Equal(ChannelKind.Telegram, request.Kind);
        Assert.Null(request.RequestedByOperatorId);

        var reply = fixture.Conversation.Messages.Last();
        Assert.Equal(MessageAuthorKind.System, reply.AuthorKind);
        Assert.Contains("482913", reply.Body.Value);
        Assert.Single(fixture.Outbox.Enqueued);
    }

    [Fact]
    public async Task HandleAsync_TheCommandWordWithAnInvalidChannelKind_RepliesWithUsage_AndCreatesNoRequest()
    {
        var fixture = CreateFixture();
        fixture.Conversation.AddVisitorMessage(
            VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("/linkidentity carrier-pigeon"), Now);

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(LinkIdentityCommandOutcome.InvalidArgument, result.Value);
        Assert.Empty(fixture.PendingLinks.All);
        var reply = fixture.Conversation.Messages.Last();
        Assert.Equal(MessageAuthorKind.System, reply.AuthorKind);
    }

    /// <summary>Ordinary conversation - the overwhelming majority of visitor messages - must not be
    /// mistaken for the command, and must produce no reply and no pending request at all.</summary>
    [Fact]
    public async Task HandleAsync_AnOrdinaryMessage_DoesNothing()
    {
        var fixture = CreateFixture();
        fixture.Conversation.AddVisitorMessage(
            VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hello, I have a question"), Now);

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(LinkIdentityCommandOutcome.NotThisCommand, result.Value);
        Assert.Empty(fixture.PendingLinks.All);
        Assert.Single(fixture.Conversation.Messages);
        Assert.Empty(fixture.Outbox.Enqueued);
    }

    [Fact]
    public async Task HandleAsync_ASystemAuthoredTrigger_IsIgnored()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            Trigger(fixture.Conversation, MessageAuthorKind.System), CancellationToken.None);

        Assert.Equal(LinkIdentityCommandOutcome.NotAVisitorMessage, result.Value);
        Assert.Empty(fixture.PendingLinks.All);
    }

    /// <summary>
    /// `CLAUDE.md` rule 5: a genuine redelivery happens only when the first attempt's own commit never
    /// actually landed (`adr/0017`: stage-then-single-save is all-or-nothing), so the conversation a
    /// redelivery sees is in the <em>same</em> state the first attempt started from - modelled here with
    /// two independent fixtures sharing one inbox, the same "the fake cannot mirror a rolled-back save"
    /// technique <c>RouteConversationToModuleHandlerTests.HandleAsync_ARedeliveredTrigger_ProducesNoSecondEffect</c>
    /// uses and explains in full. Only the outcome is asserted, never "no second row" against the fake
    /// repository - <see cref="FakeInboxChecker"/>'s own remarks on exactly why that would not be an
    /// honest claim from this fake.
    /// </summary>
    [Fact]
    public async Task HandleAsync_ARedeliveredTrigger_ProducesNoSecondEffect()
    {
        var inbox = new FakeInboxChecker();
        var messageId = Guid.NewGuid();

        var firstAttempt = CreateFixture(inbox: inbox);
        firstAttempt.Conversation.AddVisitorMessage(
            VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("/linkidentity telegram"), Now);
        var first = await firstAttempt.Handler.HandleAsync(
            Trigger(firstAttempt.Conversation, messageId: messageId), CancellationToken.None);
        Assert.Equal(LinkIdentityCommandOutcome.RequestCreated, first.Value);

        var secondAttempt = CreateFixture(inbox: inbox);
        secondAttempt.Conversation.AddVisitorMessage(
            VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("/linkidentity telegram"), Now);
        var redelivery = await secondAttempt.Handler.HandleAsync(
            Trigger(secondAttempt.Conversation, messageId: messageId), CancellationToken.None);

        Assert.Equal(LinkIdentityCommandOutcome.AlreadyProcessed, redelivery.Value);
    }
}
