using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.RouteConversationToModule;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RouteConversationToModule;

/// <summary>
/// `20-07`: the trigger-match -> start-task path, the active-task -> route-reply path, and the
/// unreachable-module escalation - at the Application level, with a <see cref="FakeModuleGateway"/>.
/// The reply-by-id parity claim itself (a widget-shaped reply and a text-channel reply against the
/// *same* rendered step producing byte-identical outbound calls) is proven end-to-end against a real
/// fake HTTP server in <c>Ago.Chat.Integration.Tests</c> - this level proves the same resolution logic
/// unit-by-unit, with fakes, per `testing.md`'s "don't reach for Testcontainers where a Domain/
/// Application unit test with fakes proves the same rule."
/// </summary>
public class RouteConversationToModuleHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly ModuleKey Calendar = new("calendar");
    private static readonly Uri EntryPoint = new("https://calendar.example.com");
    private static readonly ModuleCredential Credential = new("a-shared-secret-of-sixteen-plus-chars");

    private sealed record Fixture(
        RouteConversationToModuleHandler Handler, Conversation Conversation, FakeModuleGateway Gateway,
        FakeOutboxWriter Outbox, FakeInboxChecker Inbox, FakeChannelIdentityRepository ChannelIdentities);

    private static Fixture CreateFixture(
        bool moduleEnabled = true, Action<Conversation>? arrange = null,
        FakeModuleGateway? gateway = null, FakeInboxChecker? inbox = null,
        FakeChannelIdentityRepository? channelIdentities = null)
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        arrange?.Invoke(conversation);
        conversation.ClearDomainEvents();

        var conversations = new FakeConversationRepository();
        conversations.Seed(conversation);

        var readStore = new FakeEnabledModuleReadStore();
        if (moduleEnabled)
        {
            readStore.Seed(SiteId, new EnabledModuleSummary(Calendar, ["/booking", "book"], EntryPoint, Credential));
        }

        gateway ??= new FakeModuleGateway();
        var outbox = new FakeOutboxWriter();
        inbox ??= new FakeInboxChecker();
        channelIdentities ??= new FakeChannelIdentityRepository();

        var handler = new RouteConversationToModuleHandler(
            conversations, readStore, gateway, channelIdentities, outbox, inbox, new FakeClock(Now), new FakeIdGenerator());

        return new Fixture(handler, conversation, gateway, outbox, inbox, channelIdentities);
    }

    private static Ago.Chat.Application.UseCases.RouteConversationToModule.RouteConversationToModule Trigger(
        Conversation conversation, MessageAuthorKind authorKind = MessageAuthorKind.Visitor, int? sequence = null,
        Guid? messageId = null) =>
        new(messageId ?? Guid.NewGuid(), SiteId, conversation.Id, authorKind, sequence ?? conversation.LastSequence);

    private static ModuleStep ChoiceStep(string prompt, params (string Label, string Value)[] options) => new(
        new MessageContentKind(PrimitiveKinds.ChoiceList),
        new MessagePayload($$"""{"prompt":"{{prompt}}"}"""),
        options.Select(o => new MessageAction(o.Label, o.Value)).ToList());

    /// <summary>`19-03`: a module's own low-confidence signal - <see cref="PrimitiveKinds.Escalate"/>,
    /// no actions, `prompt` optional (a module may hand off with nothing more to say than "I don't
    /// know").</summary>
    private static ModuleStep EscalateStep(string? prompt = null) => new(
        new MessageContentKind(PrimitiveKinds.Escalate),
        prompt is null ? null : new MessagePayload($$"""{"prompt":"{{prompt}}"}"""),
        []);

    // ------------------------------------------------------------------------------------------
    // Trigger match -> start task
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_WithATriggerMatch_StartsATaskAndRecordsTheModulesStep()
    {
        var fixture = CreateFixture();
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("/booking"), Now);
        fixture.Gateway.OnStartTask = _ => new StartModuleTaskResult(
            "external-1", ChoiceStep("Which service?", ("Haircut", "svc-1"), ("Manicure", "svc-2")), false);

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.TaskStarted, result.Value);
        Assert.NotNull(fixture.Conversation.ActiveModuleTask);
        Assert.Equal(Calendar, fixture.Conversation.ActiveModuleTask!.ModuleKey);
        Assert.Equal("external-1", fixture.Conversation.ActiveModuleTask!.ExternalTaskId);

        var reply = fixture.Conversation.Messages.Last();
        Assert.Equal(MessageAuthorKind.System, reply.AuthorKind);
        Assert.Equal("Which service?\n1) Haircut\n2) Manicure\nReply with the number.", reply.Body.Value);
        Assert.NotNull(reply.Content);
    }

    /// <summary>`22-02`: the registry's own credential rides along on every call to the gateway, not
    /// merely the entry point - <c>HttpModuleGateway</c> is what turns this into the per-call signed
    /// header a module actually checks, but this handler is the one place that reads it off the
    /// registry row in the first place, so this is the boundary at which "the wrong secret got
    /// forwarded" would first become visible.</summary>
    [Fact]
    public async Task HandleAsync_WithATriggerMatch_ForwardsTheRegisteredCredentialToTheGateway()
    {
        var fixture = CreateFixture();
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("/booking"), Now);
        fixture.Gateway.OnStartTask = _ => new StartModuleTaskResult(
            "external-1", ChoiceStep("Which service?", ("Haircut", "svc-1")), false);

        await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        var call = Assert.Single(fixture.Gateway.StartCalls);
        Assert.Equal(Credential, call.Module.Credential);
    }

    [Fact]
    public async Task HandleAsync_WithNoTriggerMatch_DoesNothing()
    {
        var fixture = CreateFixture();
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hello there"), Now);
        var messagesBefore = fixture.Conversation.Messages.Count;

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.NoTriggerMatch, result.Value);
        Assert.Equal(messagesBefore, fixture.Conversation.Messages.Count);
        Assert.Null(fixture.Conversation.ActiveModuleTask);
        Assert.Empty(fixture.Gateway.StartCalls);
    }

    /// <summary>The escalation rule's "at trigger" half: the module never answers, so no task is ever
    /// started - only an apology.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheModuleIsUnreachableAtTrigger_TellsTheVisitor_AndStartsNoTask()
    {
        var fixture = CreateFixture();
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("/booking"), Now);
        fixture.Gateway.UnreachableOnStart = true;

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.ModuleUnavailableAtTrigger, result.Value);
        Assert.Null(fixture.Conversation.ActiveModuleTask);
        var reply = fixture.Conversation.Messages.Last();
        Assert.Equal(MessageAuthorKind.System, reply.AuthorKind);
        Assert.Null(reply.Content);
    }

    /// <summary>`19-03`'s own Done-when: "a visitor asking something the knowledge base does not cover
    /// gets the low-confidence escape to an operator, proven by a test." The module signals this on its
    /// very first answer - no active task ever exists to close, but the fresh one this call just
    /// started must still end up closed, not left open waiting for a reply nobody will resolve.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheModuleAnswersWithEscalateOnStart_ClosesTheTaskAndReportsEscalated()
    {
        var fixture = CreateFixture();
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("/booking is the shop open on Mars"), Now);
        // The module deliberately (or by a bug) reports `complete: false` alongside `escalate` - decision
        // 7's "cannot be suppressed by the module" is exactly the case this asserts against.
        fixture.Gateway.OnStartTask = _ => new StartModuleTaskResult(
            "external-1", EscalateStep("I'm not sure about that one."), false);

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.Escalated, result.Value);
        Assert.Null(fixture.Conversation.ActiveModuleTask);
        var reply = fixture.Conversation.Messages.Last();
        Assert.Equal(MessageAuthorKind.System, reply.AuthorKind);
        Assert.Equal("I'm not sure about that one.", reply.Body.Value);
    }

    /// <summary>The fallback text is Chat's own generic apology, never the visitor's own trigger
    /// message - showing a visitor their own last message back as "the reason" would read as a bug.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheEscalateStepCarriesNoPromptOfItsOwn_UsesTheGenericFallback_NotTheVisitorsOwnMessage()
    {
        var fixture = CreateFixture();
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("/booking is the shop open on Mars"), Now);
        fixture.Gateway.OnStartTask = _ => new StartModuleTaskResult("external-1", EscalateStep(), true);

        await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        var reply = fixture.Conversation.Messages.Last();
        Assert.DoesNotContain("Mars", reply.Body.Value);
        Assert.Equal("Let me get a team member to help with that.", reply.Body.Value);
    }

    // ------------------------------------------------------------------------------------------
    // Active task -> reply routing, the reply-by-id resolver
    // ------------------------------------------------------------------------------------------

    private static Conversation ConversationWithActiveTask(Conversation conversation, string prompt, params (string Label, string Value)[] options)
    {
        var kind = new MessageContentKind(PrimitiveKinds.ChoiceList);
        var payload = new MessagePayload($$"""{"prompt":"{{prompt}}"}""");
        var actions = options.Select(o => new MessageAction(o.Label, o.Value)).ToList();
        conversation.StartModuleTask(new ModuleTaskId(Guid.NewGuid()), Calendar, "external-1", Now, kind, payload, actions);
        return conversation;
    }

    /// <summary>The widget-shaped reply: the visitor's message carries structured content whose kind
    /// echoes the step, and whose payload is <c>{"value": "&lt;action value&gt;"}</c>.</summary>
    [Fact]
    public async Task HandleAsync_WithAWidgetShapedReply_SubmitsTheResolvedActionValue()
    {
        var fixture = CreateFixture(arrange: c => ConversationWithActiveTask(c, "Which service?", ("Haircut", "svc-1"), ("Manicure", "svc-2")));
        var content = MessageContent.Create(new MessageContentKind(PrimitiveKinds.ChoiceList), new MessagePayload("""{"value":"svc-2"}"""));
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("Manicure"), Now, content: content);
        fixture.Gateway.OnSubmitReply = _ => new SubmitModuleReplyResult(null, true);

        await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        var call = Assert.Single(fixture.Gateway.ReplyCalls);
        Assert.Equal("svc-2", call.Request.Value);
    }

    /// <summary>The text-channel reply: a bare number, resolved against the active task's own last
    /// actions - and the whole point, the identical value the widget path above submits.</summary>
    [Fact]
    public async Task HandleAsync_WithATextChannelNumericReply_ResolvesToTheSameValueAsTheWidgetReply()
    {
        var fixture = CreateFixture(arrange: c => ConversationWithActiveTask(c, "Which service?", ("Haircut", "svc-1"), ("Manicure", "svc-2")));
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("2"), Now);
        fixture.Gateway.OnSubmitReply = _ => new SubmitModuleReplyResult(null, true);

        await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        var call = Assert.Single(fixture.Gateway.ReplyCalls);
        Assert.Equal("svc-2", call.Request.Value);
    }

    [Fact]
    public async Task HandleAsync_WithAnOutOfRangeTextReply_DoesNotCallTheModule_AndLeavesTheTaskOpen()
    {
        var fixture = CreateFixture(arrange: c => ConversationWithActiveTask(c, "Which service?", ("Haircut", "svc-1")));
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("99"), Now);

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.ReplyNotResolved, result.Value);
        Assert.Empty(fixture.Gateway.ReplyCalls);
        Assert.NotNull(fixture.Conversation.ActiveModuleTask);
    }

    [Fact]
    public async Task HandleAsync_WhenTheModuleReportsAnotherStep_RecordsItAndKeepsTheTaskOpen()
    {
        var fixture = CreateFixture(arrange: c => ConversationWithActiveTask(c, "Which service?", ("Haircut", "svc-1")));
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("1"), Now);
        fixture.Gateway.OnSubmitReply = _ => new SubmitModuleReplyResult(ChoiceStep("Which time?", ("10:00", "t1")), false);

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.StepAdvanced, result.Value);
        Assert.NotNull(fixture.Conversation.ActiveModuleTask);
        Assert.Equal(new MessageContentKind(PrimitiveKinds.ChoiceList), fixture.Conversation.ActiveModuleTask!.LastStepKind);
        Assert.Equal("t1", fixture.Conversation.ActiveModuleTask!.LastStepActions.Single().Value);
    }

    [Fact]
    public async Task HandleAsync_WhenTheModuleReportsCompletion_ClosesTheTask()
    {
        var fixture = CreateFixture(arrange: c => ConversationWithActiveTask(c, "Which service?", ("Haircut", "svc-1")));
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("1"), Now);
        fixture.Gateway.OnSubmitReply = _ => new SubmitModuleReplyResult(null, true);

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.TaskCompleted, result.Value);
        Assert.Null(fixture.Conversation.ActiveModuleTask);
    }

    /// <summary>`19-03`: the *reachable-but-unsure* mirror of the unreachable-mid-task case just below -
    /// the module answers, but with the low-confidence signal instead of a further step, again with
    /// `complete: false` to prove Chat does not trust the module's own flag for this one kind.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheModuleAnswersWithEscalateMidTask_ClosesTheTask_AndReportsEscalated()
    {
        var fixture = CreateFixture(arrange: c => ConversationWithActiveTask(c, "Which service?", ("Haircut", "svc-1")));
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("1"), Now);
        fixture.Gateway.OnSubmitReply = _ => new SubmitModuleReplyResult(EscalateStep("Not sure I can help with that."), false);

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.Escalated, result.Value);
        Assert.Null(fixture.Conversation.ActiveModuleTask);
        var reply = fixture.Conversation.Messages.Last();
        Assert.Equal("Not sure I can help with that.", reply.Body.Value);
    }

    // ------------------------------------------------------------------------------------------
    // `20-09`: the verified-phone gate - a reply against a VerifiedPhoneForm step is checked
    // against `14-15`'s own evidence before the module is ever called.
    // ------------------------------------------------------------------------------------------

    private static Conversation ConversationAwaitingVerifiedPhone(Conversation conversation, string prompt = "What's your phone number?")
    {
        var kind = new MessageContentKind(PrimitiveKinds.VerifiedPhoneForm);
        var payload = new MessagePayload($$"""{"prompt":"{{prompt}}","fieldId":"phone","fieldLabel":"Phone number"}""");
        conversation.StartModuleTask(new ModuleTaskId(Guid.NewGuid()), Calendar, "external-1", Now, kind, payload, []);
        return conversation;
    }

    [Fact]
    public async Task HandleAsync_AVerifiedPhoneFormReply_WithNoVerifiedIdentity_DoesNotCallTheModule_AndTellsTheVisitorToVerify()
    {
        var fixture = CreateFixture(arrange: c => ConversationAwaitingVerifiedPhone(c));
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("+79990000001"), Now);

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.PhoneVerificationRequired, result.Value);
        Assert.Empty(fixture.Gateway.ReplyCalls);
        Assert.NotNull(fixture.Conversation.ActiveModuleTask);
        var reply = fixture.Conversation.Messages.Last();
        Assert.Equal(MessageAuthorKind.System, reply.AuthorKind);
        Assert.Contains("verify", reply.Body.Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An identity verified for a phone that reads the same but belongs to a *different*
    /// visitor must not satisfy this gate - the same "reuse, never merge" boundary `ChannelIdentity`'s
    /// own remarks establish for `14-12`'s own linking.</summary>
    [Fact]
    public async Task HandleAsync_AVerifiedPhoneFormReply_VerifiedForADifferentVisitor_DoesNotCallTheModule()
    {
        var otherVisitor = new VisitorId(Guid.NewGuid());
        var identities = new FakeChannelIdentityRepository();
        await identities.SaveAsync(
            ChannelIdentity.Link(
                new ChannelIdentityId(Guid.NewGuid()), SiteId, ChannelKind.Sms,
                new ExternalChannelAddress("+79990000001"), otherVisitor, Now.AddDays(-1)),
            CancellationToken.None);

        var fixture = CreateFixture(arrange: c => ConversationAwaitingVerifiedPhone(c), channelIdentities: identities);
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("+79990000001"), Now);

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.PhoneVerificationRequired, result.Value);
        Assert.Empty(fixture.Gateway.ReplyCalls);
    }

    [Fact]
    public async Task HandleAsync_AVerifiedPhoneFormReply_WithAVerifiedIdentity_ForwardsTheReply_CarryingThePhoneVerifiedAtTimestamp()
    {
        var verifiedAt = Now.AddDays(-1);
        var identities = new FakeChannelIdentityRepository();
        await identities.SaveAsync(
            ChannelIdentity.Link(
                new ChannelIdentityId(Guid.NewGuid()), SiteId, ChannelKind.Sms,
                new ExternalChannelAddress("+79990000001"), VisitorId, verifiedAt),
            CancellationToken.None);

        var fixture = CreateFixture(arrange: c => ConversationAwaitingVerifiedPhone(c), channelIdentities: identities);
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("+79990000001"), Now);
        fixture.Gateway.OnSubmitReply = _ => new SubmitModuleReplyResult(null, true);

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.TaskCompleted, result.Value);
        var call = Assert.Single(fixture.Gateway.ReplyCalls);
        Assert.Equal("+79990000001", call.Request.Value);
        Assert.Equal(verifiedAt, call.Request.PhoneVerifiedAt);
    }

    /// <summary>A phone that does not even parse is Calendar's own concern (its <c>PhoneNumber</c>
    /// shape check), not a second validation Chat invents - the reply is forwarded unchanged, exactly
    /// as an ordinary <c>form</c> reply always has been, carrying no verification assertion.</summary>
    [Fact]
    public async Task HandleAsync_AVerifiedPhoneFormReply_ThatDoesNotParseAsAPhoneNumber_IsForwardedUnverified_ForCalendarToReject()
    {
        var fixture = CreateFixture(arrange: c => ConversationAwaitingVerifiedPhone(c));
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("not a phone"), Now);
        fixture.Gateway.OnSubmitReply = _ => new SubmitModuleReplyResult(null, true);

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.TaskCompleted, result.Value);
        var call = Assert.Single(fixture.Gateway.ReplyCalls);
        Assert.Equal("not a phone", call.Request.Value);
        Assert.Null(call.Request.PhoneVerifiedAt);
    }

    /// <summary>The gate only ever applies to a <see cref="PrimitiveKinds.VerifiedPhoneForm"/> step -
    /// an ordinary choice reply must never carry a verification timestamp, verified identity or not.</summary>
    [Fact]
    public async Task HandleAsync_AnOrdinaryChoiceListReply_NeverCarriesAPhoneVerifiedAtTimestamp()
    {
        var identities = new FakeChannelIdentityRepository();
        await identities.SaveAsync(
            ChannelIdentity.Link(
                new ChannelIdentityId(Guid.NewGuid()), SiteId, ChannelKind.Sms,
                new ExternalChannelAddress("1"), VisitorId, Now),
            CancellationToken.None);

        var fixture = CreateFixture(
            arrange: c => ConversationWithActiveTask(c, "Which service?", ("Haircut", "svc-1")),
            channelIdentities: identities);
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("1"), Now);
        fixture.Gateway.OnSubmitReply = _ => new SubmitModuleReplyResult(null, true);

        await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        var call = Assert.Single(fixture.Gateway.ReplyCalls);
        Assert.Null(call.Request.PhoneVerifiedAt);
    }

    /// <summary>The escalation rule's "mid-task" half: the module goes unreachable while a task is
    /// active - close the task and hand off to a human.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheModuleBecomesUnreachableMidTask_ClosesTheTask_AndEscalates()
    {
        var fixture = CreateFixture(arrange: c => ConversationWithActiveTask(c, "Which service?", ("Haircut", "svc-1")));
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("1"), Now);
        fixture.Gateway.UnreachableOnReply = true;

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.Escalated, result.Value);
        Assert.Null(fixture.Conversation.ActiveModuleTask);
        var reply = fixture.Conversation.Messages.Last();
        Assert.Equal(MessageAuthorKind.System, reply.AuthorKind);
    }

    // ------------------------------------------------------------------------------------------
    // Loop guard and idempotency
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_ForANonVisitorMessage_DoesNothing()
    {
        var fixture = CreateFixture();
        var reply = fixture.Conversation.AddSystemMessage(new MessageId(Guid.NewGuid()), new MessageBody("/booking"), Now);
        fixture.Conversation.ClearDomainEvents();

        var result = await fixture.Handler.HandleAsync(
            Trigger(fixture.Conversation, MessageAuthorKind.System, reply.Sequence), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.NotAVisitorMessage, result.Value);
        Assert.Empty(fixture.Gateway.StartCalls);
    }

    /// <summary>
    /// A genuine redelivery happens only when the first attempt's own commit never actually landed
    /// (`adr/0017`: stage-then-single-save is all-or-nothing) - so the conversation a redelivery sees
    /// is in the <em>same</em> state the first attempt started from, never whatever a successfully
    /// committed first attempt would have advanced it to. Modelled here with two independent
    /// conversation snapshots sharing one inbox and one gateway - the same "the fake cannot mirror a
    /// rolled-back save" limitation <c>FakeInboxChecker</c>'s own remarks already name, and the same
    /// technique <c>SendOfflineAutoReplyHandlerTests.ARedeliveredTriggerProducesNoSecondReply</c> uses
    /// for a handler whose own effects happen to be idempotent enough to reuse one conversation - this
    /// handler's are not (a second, real call to the module - the accepted at-least-once cost this
    /// handler's own remarks document), which is exactly why two snapshots are needed here to model it
    /// honestly rather than asserting something the fake cannot actually prove.
    /// </summary>
    [Fact]
    public async Task HandleAsync_ARedeliveredTrigger_ProducesNoSecondEffect()
    {
        var gateway = new FakeModuleGateway
        {
            OnStartTask = _ => new StartModuleTaskResult("external-1", ChoiceStep("Which service?", ("Haircut", "svc-1")), false),
        };
        var inbox = new FakeInboxChecker();
        var messageId = Guid.NewGuid();

        var firstAttempt = CreateFixture(gateway: gateway, inbox: inbox);
        firstAttempt.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("/booking"), Now);
        var first = await firstAttempt.Handler.HandleAsync(
            Trigger(firstAttempt.Conversation, messageId: messageId), CancellationToken.None);
        Assert.Equal(RouteConversationToModuleOutcome.TaskStarted, first.Value);

        var secondAttempt = CreateFixture(gateway: gateway, inbox: inbox);
        secondAttempt.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("/booking"), Now);
        var redelivery = await secondAttempt.Handler.HandleAsync(
            Trigger(secondAttempt.Conversation, messageId: messageId), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.AlreadyProcessed, redelivery.Value);
    }

    [Fact]
    public async Task HandleAsync_ASuccessfulOutcome_IsOutboxedAsMessageAccepted_AndLeavesNoDomainEventsBehind()
    {
        var fixture = CreateFixture();
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("/booking"), Now);
        fixture.Conversation.ClearDomainEvents();
        fixture.Gateway.OnStartTask = _ => new StartModuleTaskResult("external-1", ChoiceStep("Which service?", ("Haircut", "svc-1")), false);

        await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        var envelope = Assert.Single(fixture.Outbox.Enqueued);
        Assert.Equal(nameof(MessageAccepted), envelope.Type);
        Assert.Empty(fixture.Conversation.DomainEvents);
    }

    [Fact]
    public async Task HandleAsync_WhenNoModuleIsEnabledForTheSite_TreatsItAsNoTriggerMatch()
    {
        var fixture = CreateFixture(moduleEnabled: false);
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("/booking"), Now);

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.NoTriggerMatch, result.Value);
    }
}
