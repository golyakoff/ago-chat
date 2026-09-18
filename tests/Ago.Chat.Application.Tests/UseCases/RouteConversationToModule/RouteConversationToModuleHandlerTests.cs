using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.CreateOperatorInvite;
using Ago.Chat.Application.UseCases.RecordVisitorContactDetail;
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
        FakeOutboxWriter Outbox, FakeInboxChecker Inbox, FakeChannelIdentityRepository ChannelIdentities,
        FakeVisitorContactDetailRepository ContactDetails, FakeAcceptanceRepository Acceptances,
        FakeDocumentRepository Documents);

    /// <summary>`25-37`/`25-39`: a freshly registered `Site` at this fixture's own `SiteId`, `Locale.En`
    /// and `WidgetConfig.Default` (so `AcceptUnverifiedPhone` is off) - the same "every existing
    /// row" default `ResolveLocaleAsync`/`ResolveModuleContextAsync` fall back to anyway when no site is
    /// seeded at all, made explicit here so a test that wants a different locale or the setting turned
    /// on has something to build on.</summary>
    private static Site DefaultSite() => new(SiteId, $"pk-{SiteId.Value:N}", []);

    private static Fixture CreateFixture(
        bool moduleEnabled = true, Action<Conversation>? arrange = null,
        FakeModuleGateway? gateway = null, FakeInboxChecker? inbox = null,
        FakeChannelIdentityRepository? channelIdentities = null, Site? site = null,
        FakeVisitorContactDetailRepository? contactDetails = null, bool seedSite = true,
        FakeAcceptanceRepository? acceptances = null, FakeDocumentRepository? documents = null)
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        arrange?.Invoke(conversation);
        conversation.ClearDomainEvents();

        var conversations = new FakeConversationRepository();
        conversations.Seed(conversation);

        var readStore = new FakeEnabledModuleReadStore();
        if (moduleEnabled)
        {
            readStore.Seed(
                SiteId, new EnabledModuleSummary(
                    Calendar, ["/booking", "book"], EntryPoint, Credential, GrantedByOwner: false, ExpiresAt: null));
        }

        gateway ??= new FakeModuleGateway();
        var outbox = new FakeOutboxWriter();
        inbox ??= new FakeInboxChecker();
        channelIdentities ??= new FakeChannelIdentityRepository();
        contactDetails ??= new FakeVisitorContactDetailRepository();
        acceptances ??= new FakeAcceptanceRepository();
        documents ??= new FakeDocumentRepository();

        var sites = new FakeSiteRepository();
        if (seedSite)
        {
            sites.Seed(site ?? DefaultSite());
        }

        var idGenerator = new FakeIdGenerator();
        var clock = new FakeClock(Now);
        // `25-138`: the identical, consent-gate-aware, rate-limited write path `RecordChannelVisitorContact`
        // (`25-151`) already reuses - constructed the same way that handler's own tests build one, sharing
        // this fixture's own `conversations`/`sites`/`contactDetails`/`acceptances`/`outbox` so a write
        // this gate makes is visible to everything else the fixture asserts against.
        var recordContactDetail = new RecordVisitorContactDetailHandler(
            conversations, contactDetails, sites, acceptances, new FakePermissionChecker(), new FakeRateLimiter(),
            new ContactDetailRateLimitOptions(), outbox, idGenerator, clock);

        var handler = new RouteConversationToModuleHandler(
            conversations, readStore, gateway, channelIdentities, outbox, inbox, clock,
            idGenerator, sites, contactDetails, acceptances, documents,
            new OperatorInviteOptions { ConsoleBaseUrl = "https://console.example.test" }, recordContactDetail);

        return new Fixture(handler, conversation, gateway, outbox, inbox, channelIdentities, contactDetails, acceptances, documents);
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

    /// <summary>`25-153`: a real module's own phone-collection step - a plain <see cref="PrimitiveKinds.Form"/>
    /// (rather than <see cref="PrimitiveKinds.VerifiedPhoneForm"/>, deliberately: that kind also trips
    /// `ContinueActiveTaskAsync`'s own, unrelated `14-15` phone-verification gate, which would entangle
    /// two independent gates in one test) whose `fieldId` is `"phone"` - the one fact this item's own
    /// consent gate actually keys on, via <see cref="PrimitiveKinds.IsPhoneCollectionStep"/>, wire-shaped
    /// exactly like `Ago.Calendar`'s own <c>ModuleStepFactory.PhoneForm</c>.</summary>
    private static ModuleStep PhoneStep(string prompt) => new(
        new MessageContentKind(PrimitiveKinds.Form),
        new MessagePayload($$"""{"prompt":"{{prompt}}","fieldId":"phone","fieldLabel":"Phone"}"""),
        []);

    /// <summary>`25-153`: a site with `WidgetConfig.RequireContactConsent` on - the one fact this item's
    /// gate keys on, the identical construction `ConsentGateDoesNotBlockConversationTests`/
    /// `GetConsentRequirementHandlerTests` already use for themselves.</summary>
    private static Site ConsentRequiredSite()
    {
        var site = new Site(SiteId, $"pk-{SiteId.Value:N}", ["https://example.test"], "Test Site", Now);
        site.UpdateWidgetConfig(new WidgetConfig(null, Position.BottomRight, requireContactConsent: true), Now);
        return site;
    }

    /// <summary>`25-153`: publishes this fixture's own `Contact`-purpose consent document - the identical
    /// <see cref="Document.Create"/>/<see cref="Document.Publish"/> pair `GetConsentRequirementHandlerTests`'s
    /// own `PublishAsync` already uses, so the version this seeds and the version
    /// <see cref="ResolveConsentGateAsync"/> (`RouteConversationToModuleHandler`'s own private read) later
    /// resolves are provably the same row, not a coincidence of two independently-typed literals.</summary>
    private static async Task PublishContactConsentDocumentAsync(FakeDocumentRepository documents, string title = "Contact Policy")
    {
        var documentKey = SiteConsentDocumentKey.For(SiteId, VisitorConsentPurpose.Contact);
        var document = Document.Create(new DocumentId(Guid.NewGuid()), documentKey);
        document.Publish(new PublishedDocumentVersionId(Guid.NewGuid()), title, "Body", Now);
        await documents.SaveAsync(document, CancellationToken.None);
    }

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

    /// <summary>`25-134`: found live 2026-09-17 - a Russian-locale site's booking reply ended with this
    /// one trailing instruction line in English, the one piece of this handler's own rendered
    /// system-message text that <see cref="PrimitiveTextRenderer"/>, not <see
    /// cref="RouteConversationToModuleHandler"/>, owns.</summary>
    [Fact]
    public async Task HandleAsync_WithATriggerMatch_OnARussianSite_RendersTheTrailingInstructionInRussian()
    {
        var site = DefaultSite();
        site.UpdateLocale(Locale.Ru, Now);
        var fixture = CreateFixture(site: site);
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("/booking"), Now);
        fixture.Gateway.OnStartTask = _ => new StartModuleTaskResult(
            "external-1", ChoiceStep("Which service?", ("Haircut", "svc-1"), ("Manicure", "svc-2")), false);

        await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        var reply = fixture.Conversation.Messages.Last();
        Assert.DoesNotContain("Reply with the number", reply.Body.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Which service?\n1) Haircut\n2) Manicure\nОтветьте номером.", reply.Body.Value);
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

    /// <summary>`25-37`: the site's own configured widget language rides the very first call to a
    /// module too, not merely a later reply - <see cref="StartModuleTaskRequest.Locale"/>'s own
    /// remarks.</summary>
    [Fact]
    public async Task HandleAsync_WithATriggerMatch_ForwardsTheSitesConfiguredLocaleToTheGateway()
    {
        var site = DefaultSite();
        site.UpdateLocale(Locale.Ru, Now);
        var fixture = CreateFixture(site: site);
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("/booking"), Now);
        fixture.Gateway.OnStartTask = _ => new StartModuleTaskResult(
            "external-1", ChoiceStep("Which service?", ("Haircut", "svc-1")), false);

        await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        var call = Assert.Single(fixture.Gateway.StartCalls);
        Assert.Equal("Ru", call.Request.Locale);
    }

    /// <summary>`25-37`: no site resolves at all (a genuinely narrow window - deleted between the
    /// trigger's own dedup check and this read) reads as the safe English default, never a hard
    /// failure of a reply this handler can otherwise still serve - <c>ResolveLocaleAsync</c>'s own
    /// remarks.</summary>
    [Fact]
    public async Task HandleAsync_WithATriggerMatch_WhenTheSiteDoesNotResolve_DefaultsTheLocaleToEnglish()
    {
        var fixture = CreateFixture(seedSite: false);
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("/booking"), Now);
        fixture.Gateway.OnStartTask = _ => new StartModuleTaskResult(
            "external-1", ChoiceStep("Which service?", ("Haircut", "svc-1")), false);

        await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        var call = Assert.Single(fixture.Gateway.StartCalls);
        Assert.Equal("En", call.Request.Locale);
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

    /// <summary>`25-66`: the same refusal as the test right above, on a Russian-locale site - before
    /// this item, `ModuleUnavailableText` was a hardcoded English `const string`, so this would have
    /// read in English regardless of the site's own configured language, the identical gap `25-64`
    /// found live for `PhoneVerificationRequiredText`.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheModuleIsUnreachableAtTrigger_OnARussianSite_TellsTheVisitor_InRussian()
    {
        var site = DefaultSite();
        site.UpdateLocale(Locale.Ru, Now);
        var fixture = CreateFixture(site: site);
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("/booking"), Now);
        fixture.Gateway.UnreachableOnStart = true;

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.ModuleUnavailableAtTrigger, result.Value);
        var reply = fixture.Conversation.Messages.Last();
        Assert.DoesNotContain("team member", reply.Body.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("сотрудник", reply.Body.Value, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>`25-66`: the same generic escalate fallback as the test right above, on a
    /// Russian-locale site - before this item, `ModuleEscalatedFallbackText` was a hardcoded English
    /// `const string`, so a Russian-configured tenant's visitor would have seen this apology in English
    /// mid-conversation, the identical gap `25-64` found live for `PhoneVerificationRequiredText`.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheEscalateStepCarriesNoPromptOfItsOwn_OnARussianSite_UsesTheGenericFallback_InRussian()
    {
        var site = DefaultSite();
        site.UpdateLocale(Locale.Ru, Now);
        var fixture = CreateFixture(site: site);
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("/booking is the shop open on Mars"), Now);
        fixture.Gateway.OnStartTask = _ => new StartModuleTaskResult("external-1", EscalateStep(), true);

        await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        var reply = fixture.Conversation.Messages.Last();
        Assert.DoesNotContain("Mars", reply.Body.Value);
        Assert.DoesNotContain("team member", reply.Body.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("сотрудник", reply.Body.Value, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>`25-37`: resent on every reply, not merely at task start -
    /// <see cref="SubmitModuleReplyRequest.Locale"/>'s own remarks on why this is never persisted on
    /// Calendar's own task.</summary>
    [Fact]
    public async Task HandleAsync_ContinuingAnActiveTask_ForwardsTheSitesConfiguredLocaleOnEveryReply()
    {
        var site = DefaultSite();
        site.UpdateLocale(Locale.Ru, Now);
        var fixture = CreateFixture(
            site: site, arrange: c => ConversationWithActiveTask(c, "Which service?", ("Haircut", "svc-1")));
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("1"), Now);
        fixture.Gateway.OnSubmitReply = _ => new SubmitModuleReplyResult(null, true);

        await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        var call = Assert.Single(fixture.Gateway.ReplyCalls);
        Assert.Equal("Ru", call.Request.Locale);
    }

    /// <summary>`25-38`/`25-39`: the most recent phone number this visitor gave earlier in the
    /// conversation rides along on a reply too - <see cref="SubmitModuleReplyRequest.KnownPhone"/>'s
    /// own remarks. Two contact details seeded, most recent one recorded last, to prove this is not
    /// merely "the first one found."</summary>
    [Fact]
    public async Task HandleAsync_ContinuingAnActiveTask_ForwardsTheVisitorsMostRecentlyKnownPhone()
    {
        var contactDetails = new FakeVisitorContactDetailRepository();
        var older = VisitorContactDetail.RecordFromVisitor(
            new VisitorContactDetailId(Guid.NewGuid()), VisitorId, VisitorContactDetailKind.Phone, "+79990000001",
            Now.AddMinutes(-10));
        var newer = VisitorContactDetail.RecordFromVisitor(
            new VisitorContactDetailId(Guid.NewGuid()), VisitorId, VisitorContactDetailKind.Phone, "+79990000002",
            Now.AddMinutes(-1));
        contactDetails.Seed(older);
        contactDetails.Seed(newer);
        var fixture = CreateFixture(
            contactDetails: contactDetails,
            arrange: c => ConversationWithActiveTask(c, "Which service?", ("Haircut", "svc-1")));
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("1"), Now);
        fixture.Gateway.OnSubmitReply = _ => new SubmitModuleReplyResult(null, true);

        await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        var call = Assert.Single(fixture.Gateway.ReplyCalls);
        Assert.Equal("+79990000002", call.Request.KnownPhone);
    }

    /// <summary>`25-38`/`25-39`: a visitor who never gave a phone number forwards <see langword="null"/>,
    /// never an invented one.</summary>
    [Fact]
    public async Task HandleAsync_ContinuingAnActiveTask_WithNoKnownPhone_ForwardsNull()
    {
        var fixture = CreateFixture(arrange: c => ConversationWithActiveTask(c, "Which service?", ("Haircut", "svc-1")));
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("1"), Now);
        fixture.Gateway.OnSubmitReply = _ => new SubmitModuleReplyResult(null, true);

        await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        var call = Assert.Single(fixture.Gateway.ReplyCalls);
        Assert.Null(call.Request.KnownPhone);
    }

    /// <summary>`25-39`: the site's own temporary setting rides along on every reply too - off by
    /// default (<see cref="DefaultSite"/>'s own `WidgetConfig.Default`), proven on here rather than
    /// merely off, since off is already every other reply test's own implicit assertion.</summary>
    [Fact]
    public async Task HandleAsync_ContinuingAnActiveTask_ForwardsTheSitesAcceptUnverifiedPhoneSetting()
    {
        var site = new Site(SiteId, $"pk-{SiteId.Value:N}", []);
        site.UpdateWidgetConfig(
            new WidgetConfig(null, Position.BottomRight, acceptUnverifiedPhone: true), Now);
        var fixture = CreateFixture(
            site: site, arrange: c => ConversationWithActiveTask(c, "Which service?", ("Haircut", "svc-1")));
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("1"), Now);
        fixture.Gateway.OnSubmitReply = _ => new SubmitModuleReplyResult(null, true);

        await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        var call = Assert.Single(fixture.Gateway.ReplyCalls);
        Assert.True(call.Request.AcceptUnverifiedPhone);
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

    /// <summary>`25-134`: the module-finished-with-no-further-step fallback - currently unreachable
    /// against the shipped booking module (it always answers with a confirmation step), but latent and
    /// worth proving directly since nothing else in this codebase exercises it.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheModuleReportsCompletionWithNoStep_AddsTheEnglishDoneMessageByDefault()
    {
        var fixture = CreateFixture(arrange: c => ConversationWithActiveTask(c, "Which service?", ("Haircut", "svc-1")));
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("1"), Now);
        fixture.Gateway.OnSubmitReply = _ => new SubmitModuleReplyResult(null, true);

        await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        var reply = fixture.Conversation.Messages.Last();
        Assert.Equal("Done - thank you.", reply.Body.Value);
    }

    [Fact]
    public async Task HandleAsync_WhenTheModuleReportsCompletionWithNoStep_OnARussianSite_AddsTheRussianDoneMessage()
    {
        var site = DefaultSite();
        site.UpdateLocale(Locale.Ru, Now);
        var fixture = CreateFixture(
            site: site, arrange: c => ConversationWithActiveTask(c, "Which service?", ("Haircut", "svc-1")));
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("1"), Now);
        fixture.Gateway.OnSubmitReply = _ => new SubmitModuleReplyResult(null, true);

        await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        var reply = fixture.Conversation.Messages.Last();
        Assert.Equal("Готово, спасибо.", reply.Body.Value);
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

    /// <summary>`25-64`: found live 2026-09-12 - a Russian-locale tenant's own booking conversation hit
    /// this one refusal in English mid-flow, the only Chat-owned system message in this handler that
    /// was not rendered in the site's own configured language.</summary>
    [Fact]
    public async Task HandleAsync_AVerifiedPhoneFormReply_WithNoVerifiedIdentity_OnARussianSite_TellsTheVisitorToVerify_InRussian()
    {
        var site = DefaultSite();
        site.UpdateLocale(Locale.Ru, Now);
        var fixture = CreateFixture(site: site, arrange: c => ConversationAwaitingVerifiedPhone(c));
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("+79990000001"), Now);

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.PhoneVerificationRequired, result.Value);
        var reply = fixture.Conversation.Messages.Last();
        Assert.DoesNotContain("verify", reply.Body.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("подтвердить", reply.Body.Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>`25-64`: `adr/0163`'s own decision, proven where it was missing - with the site's own
    /// `AcceptUnverifiedPhone` on and no verified identity, the reply reaches the module after all,
    /// unlike the immediately preceding test (identical setup, default site, off). Calendar's own
    /// `RequiresVerifiedPhone = !acceptUnverifiedPhone` is what actually decides the booking from here;
    /// this only proves Chat stopped refusing to ask it the question.</summary>
    [Fact]
    public async Task HandleAsync_AVerifiedPhoneFormReply_WithNoVerifiedIdentity_ButTheSiteAcceptsUnverifiedPhones_ForwardsTheReply_CarryingNoPhoneVerifiedAt()
    {
        var site = new Site(SiteId, $"pk-{SiteId.Value:N}", []);
        site.UpdateWidgetConfig(new WidgetConfig(null, Position.BottomRight, acceptUnverifiedPhone: true), Now);
        var fixture = CreateFixture(site: site, arrange: c => ConversationAwaitingVerifiedPhone(c));
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("+79990000001"), Now);
        fixture.Gateway.OnSubmitReply = _ => new SubmitModuleReplyResult(null, true);

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.TaskCompleted, result.Value);
        var call = Assert.Single(fixture.Gateway.ReplyCalls);
        Assert.Equal("+79990000001", call.Request.Value);
        Assert.Null(call.Request.PhoneVerifiedAt);
        Assert.True(call.Request.AcceptUnverifiedPhone);
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

    /// <summary>`25-66`: the same mid-task escalation as the test right above, on a Russian-locale
    /// site - before this item, `ModuleBecameUnreachableText` was a hardcoded English `const string`,
    /// so this apology would have read in English regardless of the site's own configured language, the
    /// identical gap `25-64` found live for `PhoneVerificationRequiredText`.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheModuleBecomesUnreachableMidTask_OnARussianSite_ClosesTheTask_AndEscalates_InRussian()
    {
        var site = DefaultSite();
        site.UpdateLocale(Locale.Ru, Now);
        var fixture = CreateFixture(site: site, arrange: c => ConversationWithActiveTask(c, "Which service?", ("Haircut", "svc-1")));
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("1"), Now);
        fixture.Gateway.UnreachableOnReply = true;

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.Escalated, result.Value);
        var reply = fixture.Conversation.Messages.Last();
        Assert.DoesNotContain("person", reply.Body.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("сотрудник", reply.Body.Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>`25-66`: the "module was disabled while this task was open" branch of
    /// `ModuleBecameUnreachableText` - a different call site from the mid-reply-unreachable test above
    /// (the `enabledModule is null` check in <c>ContinueActiveTaskAsync</c>, not the
    /// `ModuleUnreachableException` catch), reached without ever calling the gateway at all. Proven
    /// separately because `25-66`'s own fix moved this call site's own locale resolution earlier in the
    /// method (ahead of this check, not only ahead of the phone gate) - a regression here would mean
    /// that move broke rather than merely relocated it.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheModuleIsDisabledMidTask_OnARussianSite_ClosesTheTask_AndEscalates_InRussian()
    {
        var site = DefaultSite();
        site.UpdateLocale(Locale.Ru, Now);
        var fixture = CreateFixture(
            moduleEnabled: false, site: site,
            arrange: c => ConversationWithActiveTask(c, "Which service?", ("Haircut", "svc-1")));
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("1"), Now);

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.Escalated, result.Value);
        Assert.Null(fixture.Conversation.ActiveModuleTask);
        var reply = fixture.Conversation.Messages.Last();
        Assert.DoesNotContain("person", reply.Body.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("сотрудник", reply.Body.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Gateway.ReplyCalls);
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
    /// `25-121`'s own explicit "confirm, don't assume" concern: once `DeliverChannelMessageHandler`
    /// starts relaying a module task's own <see cref="MessageAuthorKind.System"/> prompt out to a real
    /// channel, could the identical <c>MessageAccepted</c> that triggers that relay also be mistaken by
    /// *this* handler for a fresh visitor trigger? No - this guard checks <see cref="MessageAuthorKind"/>
    /// alone, unconditionally, before it ever looks at <see cref="Message.Content"/> or anything else, so
    /// a System-authored message that happens to carry the exact same structured Content shape a relayed
    /// prompt carries is refused here exactly as any other System message is. The inbound half of the
    /// loop (a visitor's own reply, always authored <see cref="MessageAuthorKind.Visitor"/> by
    /// <see cref="Conversation.AddVisitorMessage"/>, hardcoded and untouched by `25-121`) was never at
    /// risk; this test is what makes that "obviously true" claim falsifiable instead of assumed.
    /// </summary>
    [Fact]
    public async Task HandleAsync_ForASystemAuthoredTriggerCarryingModuleStepContent_IsNotTreatedAsAFreshVisitorReply()
    {
        var fixture = CreateFixture();
        var content = MessageContent.Create(
            new MessageContentKind(PrimitiveKinds.ChoiceList),
            new MessagePayload("""{"prompt":"Which service?"}"""),
            [new MessageAction("Haircut", "svc-1")]);
        var reply = fixture.Conversation.AddSystemMessage(
            new MessageId(Guid.NewGuid()), new MessageBody("Which service?\n1) Haircut\nReply with the number."), Now,
            content: content);
        fixture.Conversation.ClearDomainEvents();

        var result = await fixture.Handler.HandleAsync(
            Trigger(fixture.Conversation, MessageAuthorKind.System, reply.Sequence, reply.Id.Value), CancellationToken.None);

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

    // ------------------------------------------------------------------------------------------
    // `25-153`: the PD-consent gate in front of any phone-collection step - general, not
    // calendar-specific, and proven against the item's own named risk: that the gate actually closes
    // the compliance gap (a booking cannot reach `BookEventHandler`, ago-calendar, without consent),
    // not merely that a new step renders.
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_APhoneCollectionStep_OnASiteRequiringConsentWithNoneOnFile_ShowsTheTenantsConsentChoiceInstead()
    {
        var documents = new FakeDocumentRepository();
        await PublishContactConsentDocumentAsync(documents, "Contact Policy");
        var fixture = CreateFixture(site: ConsentRequiredSite(), documents: documents);
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("/booking"), Now);
        fixture.Gateway.OnStartTask = _ => new StartModuleTaskResult("external-1", PhoneStep("What's your phone?"), false);

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        // Done-when #1: seen before any phone question, rendered as an ordinary numbered choice.
        Assert.Equal(RouteConversationToModuleOutcome.TaskStarted, result.Value);
        var reply = fixture.Conversation.Messages.Last();
        Assert.DoesNotContain("What's your phone?", reply.Body.Value);
        Assert.Contains("Contact Policy", reply.Body.Value);
        Assert.Contains("https://console.example.test/policies/", reply.Body.Value);
        Assert.Contains("1) Accept", reply.Body.Value);
        Assert.Contains("2) Decline", reply.Body.Value);
        Assert.NotNull(reply.Content);
        Assert.Equal(PrimitiveKinds.ChoiceList, reply.Content!.Kind.Value);
        // The real step was recorded even though it was never shown - see FinishStepAsync's own
        // remarks for why an eventual accept needs no second call to the module to reveal it.
        Assert.Equal(PrimitiveKinds.Form, fixture.Conversation.ActiveModuleTask!.LastStepKind!.Value.Value);
    }

    [Fact]
    public async Task HandleAsync_ConsentGate_NeverForwardsAnythingToTheModuleUntilAccepted_ThenLetsTheRealReplyThrough()
    {
        var documents = new FakeDocumentRepository();
        await PublishContactConsentDocumentAsync(documents, "Contact Policy");
        var fixture = CreateFixture(site: ConsentRequiredSite(), documents: documents);
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("/booking"), Now);
        fixture.Gateway.OnStartTask = _ => new StartModuleTaskResult("external-1", PhoneStep("What's your phone?"), false);

        // Turn 1: the trigger starts the task; the module's own phone step is withheld behind consent.
        var started = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);
        Assert.True(started.IsSuccess);
        Assert.Empty(fixture.Gateway.ReplyCalls);

        // Turn 2: the visitor types a real phone number anyway, before ever answering the consent
        // choice. It is not "1" or "2", so it resolves to neither accept nor decline - the compliance
        // claim itself: this cannot leak through to the module as if it were an answer.
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("+15550101"), Now);
        var typedEarly = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);
        Assert.Equal(RouteConversationToModuleOutcome.ReplyNotResolved, typedEarly.Value);
        Assert.Empty(fixture.Gateway.ReplyCalls);
        Assert.Empty(fixture.Acceptances.Saved);

        // Turn 3: accept - Done-when #2, records the identical acceptance fact 24-01's own mechanism
        // produces for the widget, and reveals the real phone step - still no call to the module.
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("1"), Now);
        var accepted = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);
        Assert.Equal(RouteConversationToModuleOutcome.ConsentGranted, accepted.Value);
        Assert.Empty(fixture.Gateway.ReplyCalls);
        var acceptance = Assert.Single(fixture.Acceptances.Saved);
        Assert.Equal(AcceptanceSubjectKind.Visitor, acceptance.SubjectKind);
        Assert.Equal(VisitorId.Value, acceptance.SubjectId);
        Assert.Equal(SiteConsentDocumentKey.For(SiteId, VisitorConsentPurpose.Contact), acceptance.DocumentKey);
        var revealed = fixture.Conversation.Messages.Last();
        Assert.Contains("What's your phone?", revealed.Body.Value);

        // Turn 4: Done-when #4, the actual compliance closure - only now, after consent, does a real
        // phone number ever reach `gateway.SubmitReplyAsync` (the one call this entire scenario could
        // ever use to reach `BookEventHandler` in `ago-calendar`), and this is the first and only time
        // it is called anywhere in this test.
        fixture.Gateway.OnSubmitReply = _ => new SubmitModuleReplyResult(null, true);
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("+15550101"), Now);
        var completed = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.TaskCompleted, completed.Value);
        var call = Assert.Single(fixture.Gateway.ReplyCalls);
        Assert.Equal("+15550101", call.Request.Value);
    }

    [Fact]
    public async Task HandleAsync_DecliningConsent_DoesNotCancelTheTask_AndReoffersWithAnExplanation_UntilAcceptedOnALaterAttempt()
    {
        var documents = new FakeDocumentRepository();
        await PublishContactConsentDocumentAsync(documents, "Contact Policy");
        var fixture = CreateFixture(site: ConsentRequiredSite(), documents: documents);
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("/booking"), Now);
        fixture.Gateway.OnStartTask = _ => new StartModuleTaskResult("external-1", PhoneStep("What's your phone?"), false);
        await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        // Decline: Done-when #3 - the task is not cancelled, and the same choice is re-offered with an
        // explanation of why consent is required.
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("2"), Now);
        var declined = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.ConsentDeclined, declined.Value);
        Assert.NotNull(fixture.Conversation.ActiveModuleTask);
        Assert.Equal(ModuleTaskState.Open, fixture.Conversation.ActiveModuleTask!.State);
        Assert.Empty(fixture.Acceptances.Saved);
        Assert.Empty(fixture.Gateway.ReplyCalls);
        var reoffer = fixture.Conversation.Messages.Last();
        Assert.Contains("Without your consent", reoffer.Body.Value);
        Assert.Contains("1) Accept", reoffer.Body.Value);
        Assert.Contains("2) Decline", reoffer.Body.Value);

        // A visitor who declined once can still accept on a later attempt.
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("1"), Now);
        var accepted = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.ConsentGranted, accepted.Value);
        Assert.Single(fixture.Acceptances.Saved);
        Assert.Contains("What's your phone?", fixture.Conversation.Messages.Last().Body.Value);
    }

    [Fact]
    public async Task HandleAsync_APhoneCollectionStep_OnASiteRequiringConsent_WithNoDocumentEverPublished_Escalates()
    {
        // The one gate outcome with nothing to ask about at all - required, but the tenant has never
        // published a document under this purpose's own key. Never a thrown exception or a consent
        // step with a blank title: the identical "an escape to a human always exists" posture this
        // codebase already gives an unreachable module.
        var fixture = CreateFixture(site: ConsentRequiredSite());
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("/booking"), Now);
        fixture.Gateway.OnStartTask = _ => new StartModuleTaskResult("external-1", PhoneStep("What's your phone?"), false);

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.Escalated, result.Value);
        Assert.Null(fixture.Conversation.ActiveModuleTask);
        Assert.Empty(fixture.Acceptances.Saved);
        Assert.Empty(fixture.Gateway.ReplyCalls);
    }

    [Fact]
    public async Task HandleAsync_APhoneCollectionStep_OnASiteWithConsentRequirementOff_IsCompletelyUnaffected()
    {
        // Done-when #5: the common case (the default, RequireContactConsent off) sees zero new step
        // and zero behaviour change - DefaultSite() carries WidgetConfig.Default, the same fixture every
        // other test in this file already uses.
        var fixture = CreateFixture();
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("/booking"), Now);
        fixture.Gateway.OnStartTask = _ => new StartModuleTaskResult("external-1", PhoneStep("What's your phone?"), false);

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.TaskStarted, result.Value);
        var reply = fixture.Conversation.Messages.Last();
        Assert.Equal("What's your phone?", reply.Body.Value);
        Assert.Equal(PrimitiveKinds.Form, reply.Content!.Kind.Value);
        Assert.Empty(fixture.Acceptances.Saved);
    }

    // ------------------------------------------------------------------------------------------
    // `25-138`: the server-side name+phone gate - a Telegram/MAX visitor with no name+phone on file is
    // asked for one, as a `form`-primitive step, before their first reply ever reaches a module.
    // ------------------------------------------------------------------------------------------

    private static ChannelIdentity GatedIdentity(ChannelKind kind, VisitorId visitorId) =>
        ChannelIdentity.Link(new ChannelIdentityId(Guid.NewGuid()), SiteId, kind, new ExternalChannelAddress("ext-1"), visitorId, Now);

    private static VisitorContactDetail ExistingPhone(VisitorId visitorId) =>
        VisitorContactDetail.RecordFromVisitor(
            new VisitorContactDetailId(Guid.NewGuid()), visitorId, VisitorContactDetailKind.Phone, "+15550100", Now);

    private static VisitorContactDetail ExistingName(VisitorId visitorId) =>
        VisitorContactDetail.RecordFromVisitor(
            new VisitorContactDetailId(Guid.NewGuid()), visitorId, VisitorContactDetailKind.Name, "Jamie", Now);

    [Theory]
    [InlineData(ChannelKind.Telegram)]
    [InlineData(ChannelKind.Max)]
    public async Task HandleAsync_AGatedChannelVisitorWithNoContactOnFile_IsAskedForAPhoneFirst_AndNeverReachesTheModule(
        ChannelKind channel)
    {
        var channelIdentities = new FakeChannelIdentityRepository();
        await channelIdentities.SaveAsync(GatedIdentity(channel, VisitorId), CancellationToken.None);
        var fixture = CreateFixture(channelIdentities: channelIdentities);
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("/booking"), Now);

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        // Done-when #1: asked for one, as a `form`-primitive step, before the module is ever reached.
        Assert.Equal(RouteConversationToModuleOutcome.TaskStarted, result.Value);
        Assert.Empty(fixture.Gateway.StartCalls);
        Assert.NotNull(fixture.Conversation.ActiveModuleTask);
        var reply = fixture.Conversation.Messages.Last();
        Assert.Equal(PrimitiveKinds.Form, reply.Content!.Kind.Value);
        Assert.Contains("phone", reply.Body.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.ContactDetails.All);
    }

    [Fact]
    public async Task HandleAsync_AnsweringTheGatesPhoneStep_AsksForANameNext_StillWithoutReachingTheModule()
    {
        var channelIdentities = new FakeChannelIdentityRepository();
        await channelIdentities.SaveAsync(GatedIdentity(ChannelKind.Telegram, VisitorId), CancellationToken.None);
        var fixture = CreateFixture(channelIdentities: channelIdentities);
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("/booking"), Now);
        await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("+15550100"), Now);
        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.StepAdvanced, result.Value);
        Assert.Empty(fixture.Gateway.StartCalls);
        var phone = Assert.Single(fixture.ContactDetails.All);
        Assert.Equal(VisitorContactDetailKind.Phone, phone.Kind);
        Assert.Equal("+15550100", phone.Value);
        var reply = fixture.Conversation.Messages.Last();
        Assert.Equal(PrimitiveKinds.Form, reply.Content!.Kind.Value);
        Assert.Contains("name", reply.Body.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_CompletingTheNamePhoneGate_ForwardsTheOriginalTriggerToTheModule_Unmodified()
    {
        var channelIdentities = new FakeChannelIdentityRepository();
        await channelIdentities.SaveAsync(GatedIdentity(ChannelKind.Telegram, VisitorId), CancellationToken.None);
        var fixture = CreateFixture(channelIdentities: channelIdentities);
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("/booking"), Now);
        await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("+15550100"), Now);
        await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        fixture.Gateway.OnStartTask = _ => new StartModuleTaskResult(
            "external-1", ChoiceStep("Which service?", ("Haircut", "svc-1")), false);
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("Jamie"), Now);
        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.TaskStarted, result.Value);
        var call = Assert.Single(fixture.Gateway.StartCalls);
        // The visitor's own original booking request - never "Jamie", the reply that merely happened to
        // clear the gate - is what finally reaches the module (this item's own "must never eat the
        // visitor's first real message, only precede it").
        Assert.Equal("/booking", call.Request.TriggerText);
        Assert.Equal(Calendar, fixture.Conversation.ActiveModuleTask!.ModuleKey);
        Assert.Equal("external-1", fixture.Conversation.ActiveModuleTask!.ExternalTaskId);
        var name = Assert.Single(fixture.ContactDetails.All, d => d.Kind == VisitorContactDetailKind.Name);
        Assert.Equal("Jamie", name.Value);
        var reply = fixture.Conversation.Messages.Last();
        Assert.Contains("Which service?", reply.Body.Value);
    }

    [Fact]
    public async Task HandleAsync_AGatedChannelVisitorWithNameAndPhoneAlreadyOnFile_SkipsTheGateEntirely()
    {
        var channelIdentities = new FakeChannelIdentityRepository();
        await channelIdentities.SaveAsync(GatedIdentity(ChannelKind.Telegram, VisitorId), CancellationToken.None);
        var contactDetails = new FakeVisitorContactDetailRepository();
        contactDetails.Seed(ExistingPhone(VisitorId));
        contactDetails.Seed(ExistingName(VisitorId));
        var fixture = CreateFixture(channelIdentities: channelIdentities, contactDetails: contactDetails);
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("/booking"), Now);
        fixture.Gateway.OnStartTask = _ => new StartModuleTaskResult(
            "external-1", ChoiceStep("Which service?", ("Haircut", "svc-1")), false);

        // Done-when #2: forwarded straight through - no gate step is ever shown.
        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.TaskStarted, result.Value);
        var call = Assert.Single(fixture.Gateway.StartCalls);
        Assert.Equal("/booking", call.Request.TriggerText);
        Assert.Contains("Which service?", fixture.Conversation.Messages.Last().Body.Value);
    }

    [Fact]
    public async Task HandleAsync_AWidgetVisitorWithNoContactOnFile_IsCompletelyUnaffectedByTheContactGate()
    {
        // Done-when #4: a widget visitor never links a ChannelIdentity at all (ChannelIdentity's own
        // remarks) - CreateFixture's own default (an empty FakeChannelIdentityRepository) already is
        // this case; asserted here explicitly rather than left implicit in every other test's default.
        var fixture = CreateFixture();
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("/booking"), Now);
        fixture.Gateway.OnStartTask = _ => new StartModuleTaskResult(
            "external-1", ChoiceStep("Which service?", ("Haircut", "svc-1")), false);

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.TaskStarted, result.Value);
        Assert.Single(fixture.Gateway.StartCalls);
        Assert.Empty(fixture.ContactDetails.All);
    }

    [Fact]
    public async Task HandleAsync_AChannelWithNoNameForThisGate_IsNeverGated_EvenWithNoContactOnFile()
    {
        // The backlog item's own explicit instruction: gated on ChannelKind by name, never on "not the
        // widget" - a channel with its own equivalent capture mechanism (Sms, here) must stay excluded
        // by not being named, not merely by accident of not being the widget.
        var channelIdentities = new FakeChannelIdentityRepository();
        await channelIdentities.SaveAsync(GatedIdentity(ChannelKind.Sms, VisitorId), CancellationToken.None);
        var fixture = CreateFixture(channelIdentities: channelIdentities);
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("/booking"), Now);
        fixture.Gateway.OnStartTask = _ => new StartModuleTaskResult(
            "external-1", ChoiceStep("Which service?", ("Haircut", "svc-1")), false);

        var result = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.TaskStarted, result.Value);
        Assert.Single(fixture.Gateway.StartCalls);
        Assert.Empty(fixture.ContactDetails.All);
    }

    [Fact]
    public async Task HandleAsync_TheContactGatesPhoneStep_OnASiteRequiringConsent_ShowsTheConsentChoiceFirst_ReusingThatGateUnmodified()
    {
        // Done-when #3: `25-153`'s own consent gate applies to this gate's own phone step too - reused,
        // not duplicated. Asked before phone (never before name, which carries no PD needing consent).
        var documents = new FakeDocumentRepository();
        await PublishContactConsentDocumentAsync(documents, "Contact Policy");
        var channelIdentities = new FakeChannelIdentityRepository();
        await channelIdentities.SaveAsync(GatedIdentity(ChannelKind.Telegram, VisitorId), CancellationToken.None);
        var fixture = CreateFixture(site: ConsentRequiredSite(), documents: documents, channelIdentities: channelIdentities);
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("/booking"), Now);

        var started = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);
        Assert.Equal(RouteConversationToModuleOutcome.TaskStarted, started.Value);
        Assert.Empty(fixture.Gateway.StartCalls);
        var consentPrompt = fixture.Conversation.Messages.Last();
        Assert.Contains("Contact Policy", consentPrompt.Body.Value);
        Assert.Equal(PrimitiveKinds.ChoiceList, consentPrompt.Content!.Kind.Value);
        // The gate's own real phone step was recorded, unshown - the identical "record now, reveal on
        // accept" shape `25-153`'s own gate already established.
        Assert.Equal(PrimitiveKinds.Form, fixture.Conversation.ActiveModuleTask!.LastStepKind!.Value.Value);

        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("1"), Now);
        var accepted = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.ConsentGranted, accepted.Value);
        Assert.Single(fixture.Acceptances.Saved);
        Assert.Empty(fixture.ContactDetails.All);
        Assert.Contains("phone", fixture.Conversation.Messages.Last().Body.Value, StringComparison.OrdinalIgnoreCase);

        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("+15550100"), Now);
        var recorded = await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        Assert.Equal(RouteConversationToModuleOutcome.StepAdvanced, recorded.Value);
        var phone = Assert.Single(fixture.ContactDetails.All);
        Assert.Equal(VisitorContactDetailKind.Phone, phone.Kind);
        Assert.Contains("name", fixture.Conversation.Messages.Last().Body.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_TheRecordedContact_IsTheOrdinaryVisitorContactDetailShape_TheSameEveryOtherSourceProduces()
    {
        // Done-when #5: the identical VisitorContactDetail shape/write-path every other source already
        // produces - RecordFromVisitor, Source.Visitor, unverified - findable by ResolveKnownPhoneAsync
        // and by GetOperatorQueueHandler's own name lookup exactly as any other visitor-submitted detail.
        var channelIdentities = new FakeChannelIdentityRepository();
        await channelIdentities.SaveAsync(GatedIdentity(ChannelKind.Telegram, VisitorId), CancellationToken.None);
        var fixture = CreateFixture(channelIdentities: channelIdentities);
        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("/booking"), Now);
        await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        fixture.Conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("+15550100"), Now);
        await fixture.Handler.HandleAsync(Trigger(fixture.Conversation), CancellationToken.None);

        var phone = Assert.Single(fixture.ContactDetails.All);
        Assert.Equal(VisitorId, phone.VisitorId);
        Assert.Equal(VisitorContactDetailKind.Phone, phone.Kind);
        Assert.Equal(VisitorContactDetailSource.Visitor, phone.Source);
        Assert.Null(phone.RecordedByOperatorId);
        Assert.False(phone.Verified);
    }

    // ------------------------------------------------------------------------------------------
    // `25-34`: retry-once-on-conflict, at the level a real Postgres race is expensive to exercise
    // for every branch - CloseConversationHandler's own established shape, reused here. The real
    // `xmin`/message-sequence race itself is Ago.Chat.Concurrency.Tests.RouteConversationToModuleConcurrencyTests's
    // job; this is the fast, deterministic proof that the retry wiring itself - reload, reapply,
    // one bounded retry, then a clean result - does what CloseConversationHandlerTests's own
    // MarkConversationReadHandlerTests precedent (ConflictingConversationRepository) already proves
    // for that handler's identical shape.
    // ------------------------------------------------------------------------------------------

    private static Conversation FreshConversationWithActiveTaskAndTrigger(Guid conversationId)
    {
        var conversation = Conversation.Start(new ConversationId(conversationId), SiteId, VisitorId, Now);
        ConversationWithActiveTask(conversation, "Which service?", ("Haircut", "svc-1"));
        conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("1"), Now);
        conversation.ClearDomainEvents();
        return conversation;
    }

    [Fact]
    public async Task HandleAsync_WhenTheConversationSaveConflictsOnce_RetriesAgainstFreshState_AndSucceeds()
    {
        var conversationId = Guid.NewGuid();
        var repository = new ConflictingConversationRepository(
            () => FreshConversationWithActiveTaskAndTrigger(conversationId), failNextSaves: 1);
        var readStore = new FakeEnabledModuleReadStore();
        readStore.Seed(
            SiteId, new EnabledModuleSummary(Calendar, ["/booking"], EntryPoint, Credential, GrantedByOwner: false, ExpiresAt: null));
        var gateway = new FakeModuleGateway { OnSubmitReply = _ => new SubmitModuleReplyResult(null, true) };
        var sites = new FakeSiteRepository();
        sites.Seed(DefaultSite());
        var outbox = new FakeOutboxWriter();
        var idGenerator = new FakeIdGenerator();
        var clock = new FakeClock(Now);
        var contactDetailsRepository = new FakeVisitorContactDetailRepository();
        var acceptances = new FakeAcceptanceRepository();
        // `25-138`: never actually exercised by this test - the active task's own `ExternalTaskId`
        // (`ConversationWithActiveTask`'s own default, not this gate's sentinel) means
        // ContinueActiveTaskAsync's new gate check never diverts here - but the dependency still has to
        // exist, so it is built the same way `CreateFixture`'s own remarks build one.
        var recordContactDetail = new RecordVisitorContactDetailHandler(
            repository, contactDetailsRepository, sites, acceptances, new FakePermissionChecker(), new FakeRateLimiter(),
            new ContactDetailRateLimitOptions(), outbox, idGenerator, clock);
        var handler = new RouteConversationToModuleHandler(
            repository, readStore, gateway, new FakeChannelIdentityRepository(), outbox, new FakeInboxChecker(),
            clock, idGenerator, sites, contactDetailsRepository,
            acceptances, new FakeDocumentRepository(),
            new OperatorInviteOptions { ConsoleBaseUrl = "https://console.example.test" }, recordContactDetail);

        var result = await handler.HandleAsync(
            new Application.UseCases.RouteConversationToModule.RouteConversationToModule(
                Guid.NewGuid(), SiteId, new ConversationId(conversationId), MessageAuthorKind.Visitor, TriggerSequence: 1),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? $"{result.Error!.Value.Code}: {result.Error!.Value.Message}" : "success");
        Assert.Equal(RouteConversationToModuleOutcome.TaskCompleted, result.Value);
        // One failed attempt, one retry that succeeded - proves the retry actually ran, not that the
        // fake happened not to be exercised.
        Assert.Equal(2, repository.SaveCount);
    }

    [Fact]
    public async Task HandleAsync_WhenTheConversationSaveConflictsTwice_ReturnsConcurrencyConflict_NotAnUnhandledException()
    {
        var conversationId = Guid.NewGuid();
        var repository = new ConflictingConversationRepository(
            () => FreshConversationWithActiveTaskAndTrigger(conversationId), failNextSaves: 2);
        var readStore = new FakeEnabledModuleReadStore();
        readStore.Seed(
            SiteId, new EnabledModuleSummary(Calendar, ["/booking"], EntryPoint, Credential, GrantedByOwner: false, ExpiresAt: null));
        var gateway = new FakeModuleGateway { OnSubmitReply = _ => new SubmitModuleReplyResult(null, true) };
        var sites = new FakeSiteRepository();
        sites.Seed(DefaultSite());
        var outbox = new FakeOutboxWriter();
        var idGenerator = new FakeIdGenerator();
        var clock = new FakeClock(Now);
        var contactDetailsRepository = new FakeVisitorContactDetailRepository();
        var acceptances = new FakeAcceptanceRepository();
        // `25-138`: never actually exercised by this test - the active task's own `ExternalTaskId`
        // (`ConversationWithActiveTask`'s own default, not this gate's sentinel) means
        // ContinueActiveTaskAsync's new gate check never diverts here - but the dependency still has to
        // exist, so it is built the same way `CreateFixture`'s own remarks build one.
        var recordContactDetail = new RecordVisitorContactDetailHandler(
            repository, contactDetailsRepository, sites, acceptances, new FakePermissionChecker(), new FakeRateLimiter(),
            new ContactDetailRateLimitOptions(), outbox, idGenerator, clock);
        var handler = new RouteConversationToModuleHandler(
            repository, readStore, gateway, new FakeChannelIdentityRepository(), outbox, new FakeInboxChecker(),
            clock, idGenerator, sites, contactDetailsRepository,
            acceptances, new FakeDocumentRepository(),
            new OperatorInviteOptions { ConsoleBaseUrl = "https://console.example.test" }, recordContactDetail);

        var result = await handler.HandleAsync(
            new Application.UseCases.RouteConversationToModule.RouteConversationToModule(
                Guid.NewGuid(), SiteId, new ConversationId(conversationId), MessageAuthorKind.Visitor, TriggerSequence: 1),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.ConcurrencyConflict", result.Error!.Value.Code);
        // `6-08`'s own bound, carried in: the retry stops here, it never loops a third time.
        Assert.Equal(2, repository.SaveCount);
    }

    /// <summary>Mirrors `MarkConversationReadHandlerTests`' own
    /// <c>ConflictingConversationRepository</c> - makes the first <c>failNextSaves</c> saves lose an
    /// optimistic-concurrency race, and hands back a <em>freshly built</em> aggregate on every load, the
    /// way a real reload after <c>ChangeTracker.Clear()</c> does. Reusing the already-mutated instance
    /// would let a retry test pass for the wrong reason: the retry would find the state already moved
    /// and take a no-op path rather than genuinely reapplying its own mutation.</summary>
    private sealed class ConflictingConversationRepository(Func<Conversation> load, int failNextSaves)
        : IConversationRepository
    {
        private int _failuresRemaining = failNextSaves;

        public int SaveCount { get; private set; }

        public Task<Conversation?> GetByIdAsync(ConversationId id, CancellationToken cancellationToken) =>
            Task.FromResult<Conversation?>(load());

        public Task<IReadOnlyDictionary<ConversationId, Conversation>> GetByIdsAsync(
            IReadOnlyCollection<ConversationId> ids, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Conversation?> GetActiveForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Conversation>> GetAssignedToOperatorAsync(OperatorId operatorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Conversation>> GetWaitingForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveAsync(Conversation conversation, CancellationToken cancellationToken)
        {
            SaveCount++;
            if (_failuresRemaining > 0)
            {
                _failuresRemaining--;
                throw new ConversationConcurrencyConflictException(conversation.Id);
            }

            return Task.CompletedTask;
        }
    }
}
