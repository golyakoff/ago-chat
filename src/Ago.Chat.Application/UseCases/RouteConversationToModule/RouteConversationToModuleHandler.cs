using System.Text.Json;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Application.UseCases.CreateOperatorInvite;
using Ago.Chat.Application.UseCases.RecordVisitorContactDetail;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RouteConversationToModule;

/// <summary>
/// `20-07`/`adr/0065`: the trigger-match -> start-task path and the active-task -> route-reply path,
/// both in one handler because they are the two faces of one decision - "does this conversation's next
/// visitor input go to a module, and if so, which" - and a conversation is never in both states at
/// once (`adr/0065` decision 7's "at most one active task").
///
/// <para><b>Driven by <c>MessageAccepted</c>, the identical shape `14-04`'s <c>SendOfflineAutoReply</c>
/// established.</b> The alternative - deciding inline in <c>SendVisitorMessageHandler</c>, before the
/// message is even durable - was rejected for the same reason that handler's own remarks give: it
/// enqueues onto `4-05`'s pipeline and never touches Postgres itself, so a decision made there would be
/// judging a conversation state (whether a task is active, what the last step's actions were) that has
/// not committed yet. Reacting to the message once it is durable also means both the widget and every
/// `14-0x` channel converge on the identical code path - "the same flow completes over a text-only
/// channel" (backlog item's own Done-when) falls out of this for free, exactly as `SendOfflineAutoReply`'s
/// own remarks describe for its reply.</para>
///
/// <para><b>The reply-value resolver is one function, reused for every kind and every channel -
/// the item's own explicit constraint.</b> See <see cref="ResolveReplyValue"/>: it does not branch on
/// <see cref="MessageContentKind"/> beyond asking <see cref="PrimitiveKinds.IsChoiceShaped"/> once
/// (which itself lives in Domain, not here), and it treats a widget's structured reply and a text
/// channel's bare number identically once each has produced a value - which is exactly what
/// <c>ReplyParityTests</c> (Ago.Chat.Integration.Tests) proves by asserting the outbound calls are
/// byte-identical.</para>
///
/// <para><b>Idempotency (`CLAUDE.md` rule 5), and the one honestly-stated gap in it.</b>
/// <see cref="IInboxChecker.TryRecordAndSaveAsync"/> is now called first and alone, before any
/// mutation of the tracked <see cref="Conversation"/> is even attempted - `25-34`'s own fix, after the
/// original "stage everything, then one combined save" shape (still how `SendOfflineAutoReplyHandler`
/// works, for a handler that never needs to retry its own aggregate save) turned out to let a genuine
/// `xmin` conflict on the <see cref="Conversation"/> row surface as a raw, unhandled EF exception with
/// nothing to catch it (see <see cref="AddSystemMessageAndSaveAsync"/>'s own remarks for the full
/// shape). What no ordering of these two saves can make idempotent is the call to
/// <see cref="IModuleGateway"/> itself, which happens <em>before</em> either one, before anything is
/// staged at all (an HTTP call cannot sit inside a database transaction - `CLAUDE.md`'s own boundary
/// rules). A redelivered <c>MessageAccepted</c> - rare, but possible under `adr/0017`'s at-least-once
/// delivery - can therefore still cause a second, wasted call to the module (a second external task
/// started, or a reply resubmitted) whose result is simply discarded once the dedup check above reports
/// "already recorded". This is an accepted, at-least-once cost identical in kind to every other side
/// effect this codebase performs before its own dedup point (`resilience.md`'s own idempotency-key
/// discipline is what keeps it *safe* on the module's side, not what makes it *free*) - stated here
/// rather than left implicit, per the backlog item's own instruction.</para>
/// </summary>
public sealed class RouteConversationToModuleHandler(
    IConversationRepository conversations,
    IEnabledModuleReadStore moduleReadStore,
    IModuleGateway gateway,
    IChannelIdentityRepository channelIdentities,
    IOutboxWriter outbox,
    IInboxChecker inbox,
    IClock clock,
    IIdGenerator idGenerator,
    ISiteRepository sites,
    IVisitorContactDetailRepository contactDetails,
    IAcceptanceRepository acceptances,
    IDocumentRepository documents,
    OperatorInviteOptions consoleOptions,
    RecordVisitorContactDetailHandler recordContactDetail)
{
    public const string ConsumerName = "module-task-routing";

    /// <summary>
    /// `25-138`: the sentinel <see cref="ModuleTask.ExternalTaskId"/> this gate's own, Chat-only
    /// <see cref="ModuleTask"/> is started with - the one, deliberate, narrowly-scoped exception to that
    /// field's own "opaque, never generated or interpreted by Chat" contract. Every other
    /// <see cref="ModuleTask"/> in this codebase owes that id to a real module (`gateway.StartTaskAsync`'s
    /// own return value) because a reply against it is eventually resubmitted to that same module
    /// (<c>gateway.SubmitReplyAsync</c>); this gate's own two steps never reach a module at all - see
    /// <see cref="OpenContactGateAsync"/>'s own remarks - so there is no real id to store, and this
    /// constant exists only so <see cref="ContinueActiveTaskAsync"/> can recognise "the active task is
    /// this gate, not a module's" without adding a field this aggregate does not otherwise need. This
    /// sentinel is discarded, together with the gate task itself, the instant a real module is finally
    /// engaged - <see cref="ContinueContactGateReplyAsync"/>'s own <c>StartRealModuleTaskAsync</c> call
    /// replaces it with a genuine <see cref="ModuleTask"/> carrying the module's own real id.</summary>
    private const string ContactGateExternalTaskId = "chat-contact-gate";

    /// <summary>
    /// `25-138`: the one JSON field this gate's own two <see cref="PrimitiveKinds.Form"/> payloads carry
    /// that neither <see cref="PrimitiveTextRenderer"/> nor <see cref="PrimitiveKinds.IsPhoneCollectionStep"/>
    /// ever reads - the trigger message's own <see cref="Message.Sequence"/>, so the visitor's original
    /// reply (the one this gate must never itself consume, per the backlog item's own "must never eat the
    /// visitor's first real message, only precede it") can be found again and handed to the module,
    /// unchanged, the instant this gate clears. Chat is both the sole producer and the sole consumer of
    /// this field for as long as the gate task stays open - it is gone, together with the gate task
    /// itself, the moment a real module step (with its own real fields, and none of this one) replaces
    /// it.
    /// </summary>
    private const string ContactGateTriggerSequenceField = "_gateTriggerSequence";

    /// <summary>Opaque sentinel <see cref="MessageAction.Value"/>s for `25-153`'s own consent gate -
    /// the one case in this codebase where <em>Chat itself</em> is the producer of a
    /// <see cref="PrimitiveKinds.ChoiceList"/> step, rather than relaying a module's. Chosen, not
    /// generated, because nothing downstream ever needs to look them up again the way a module's own
    /// ids do - <see cref="ContinueActiveTaskAsync"/>'s own gate branch is the only reader, comparing
    /// them by value the instant <see cref="ChoiceReplyTextResolver.Resolve"/> hands one back.</summary>
    private const string ConsentAcceptValue = "consent-accept";

    private const string ConsentDeclineValue = "consent-decline";

    /// <summary>`25-66`: the second of this class's four visitor-facing system-message texts to gain
    /// a <paramref name="locale"/> parameter - `PhoneVerificationRequiredText`'s own remarks record why
    /// it, alone, was fixed first (`25-64`'s own narrower scope) and why the other three were deferred
    /// here rather than folded into that change. Same shape: `Locale.Ru` gets natural Russian wording,
    /// every other locale keeps the original English.</summary>
    private static string ModuleUnavailableText(string locale) => locale == nameof(Locale.Ru)
        ? "Извините, сейчас это недоступно — скоро с вами свяжется сотрудник."
        : "Sorry, that's not available right now - a team member will help you shortly.";

    /// <summary>`25-66`: see <see cref="ModuleUnavailableText"/>'s own remarks - identical shape, the
    /// text shown when a task that was active becomes unreachable (the module was disabled mid-task, or
    /// a live call to it failed) rather than when starting a new one fails.</summary>
    private static string ModuleBecameUnreachableText(string locale) => locale == nameof(Locale.Ru)
        ? "Извините, что-то пошло не так с нашей стороны — дальше вам поможет сотрудник."
        : "Sorry, something went wrong on our end - a person will take over from here.";

    /// <summary>`20-09`/`25-64`: what a visitor sees when a <see cref="PrimitiveKinds.VerifiedPhoneForm"/>
    /// reply names a phone number this system has not yet proven they can be reached on, on a tenant
    /// that has not opted into `adr/0163`'s relaxation. Deliberately actionable rather than a generic
    /// refusal - see <see cref="ContinueActiveTaskAsync"/>'s own remarks for exactly what "verify it"
    /// means operationally today (no widget popup exists yet; `14-15`'s own HTTP endpoints are the real
    /// mechanism, driven directly until one does).
    ///
    /// <para><b>Localized first, ahead of this class's other three system-message texts.</b> Found live
    /// 2026-09-12: a real tenant's operator watched their own Russian booking conversation hit this one
    /// English sentence mid-flow. `ModuleUnavailableText`/`ModuleBecameUnreachableText`/
    /// `ModuleEscalatedFallbackText` shared the identical gap and were deliberately not fixed in the same
    /// change - this text was the one actually exercised by the live report that found it, and
    /// generalizing the fix to all four text constants in this class in the same change would have been
    /// scope that item never asked for (CLAUDE.md rule 15). `25-66` is where the other three were fixed,
    /// the identical shape, once it had its own number.</para>
    ///
    /// <para><b>Worded to avoid "book".</b> The original text's "Before I can book that" is the word
    /// `MessageOpacityTests.NoProductAssembly_NamesAnotherProductsDomain` exists to flag - not because
    /// this sentence structurally names a booking (it is generic phone-verification copy any module
    /// with a `VerifiedPhoneForm` step could trigger, calendar or otherwise), but because turning this
    /// text from a `const string` field into a real method moved its `ldstr` out of a compiler-generated
    /// lambda closure (which the scanner skips outright, `MessageOpacityRule.IsCompilerGenerated`'s own
    /// remarks) and into a method the scanner actually walks - the identical coincidental-collision
    /// shape `MessageOpacityExemptions`' own "slot" entry describes, caught here before it needed one.
    /// Rewording costs nothing the exemption list would not also have cost, and it is the remedy that
    /// class's own doc comment prefers when the choice is real.</para></summary>
    private static string PhoneVerificationRequiredText(string locale) => locale == nameof(Locale.Ru)
        ? "Прежде чем оформить запись, нужно подтвердить этот номер телефона — мы отправим код по SMS или позвоним."
        : "Before I can complete that, please verify this phone number - we'll send you a code by SMS or call.";

    /// <summary>`19-03`/`25-66`: the fallback <see cref="PrimitiveTextRenderer.Render"/> falls back to
    /// when a module's own escalate step carries no <c>payload.prompt</c> of its own. Deliberately not
    /// the visitor's own trigger/reply text (which is what every other kind's fallback is) - showing a
    /// visitor their own last message back at them as the "reason" for handing off reads as a bug, not
    /// as an apology. `25-66`: localized the same way its three siblings above now are, threaded through
    /// <see cref="FinishStepAsync"/> since this is the one text both <see cref="TryStartTaskAsync"/> and
    /// <see cref="ContinueActiveTaskAsync"/> can reach, by way of that shared method.</summary>
    private static string ModuleEscalatedFallbackText(string locale) => locale == nameof(Locale.Ru)
        ? "Сейчас подключу сотрудника, чтобы помочь с этим."
        : "Let me get a team member to help with that.";

    /// <summary>`25-153`: what a visitor sees the first time this gate withholds a phone-collection
    /// step, and (with <paramref name="explainDecline"/>) every time it re-offers the identical choice
    /// after a decline. <paramref name="title"/>/<paramref name="link"/> are the tenant's own document's
    /// - never AGO's words - the exact `adr/0076` split the widget's own consent checkbox label already
    /// draws: this sentence is UI chrome asking the visitor to look at the tenant's own document, not
    /// AGO stating what that document says or asserting a policy on the tenant's behalf.</summary>
    private static string ConsentPromptText(string locale, string title, string link, bool explainDecline)
    {
        var reason = explainDecline ? ConsentDeclinedExplanationText(locale) + " " : string.Empty;
        return reason + (locale == nameof(Locale.Ru)
            ? $"Чтобы продолжить, пожалуйста, ознакомьтесь с документом «{title}»: {link}"
            : $"Before we continue, please review {title}: {link}");
    }

    private static string ConsentDeclinedExplanationText(string locale) => locale == nameof(Locale.Ru)
        ? "Без вашего согласия мы не можем записать номер телефона, и мы не сможем продолжить."
        : "Without your consent we can't record a phone number, so we can't continue.";

    /// <summary>`25-153`: the one place this gate has nothing to show - required, but the tenant has
    /// never published a document under this purpose's own key (<see cref="ResolveConsentGateAsync"/>'s
    /// own remarks). The identical "an escape to a human always exists" posture
    /// <see cref="ModuleBecameUnreachableText"/> already takes for a module gone unreachable, applied
    /// here to a dead end the module itself could not have anticipated or caused.</summary>
    private static string ConsentUnavailableText(string locale) => locale == nameof(Locale.Ru)
        ? "Извините, сейчас мы не можем запросить согласие — скоро с вами свяжется сотрудник."
        : "Sorry, we can't ask for your consent right now - a team member will help you shortly.";

    /// <summary>`25-153`: the two <see cref="MessageAction"/>s this gate's own <see cref="PrimitiveKinds.ChoiceList"/>
    /// step offers - labels a text renderer numbers 1/2 exactly like any other choice-shaped step
    /// (<see cref="PrimitiveTextRenderer"/>'s own remarks), values that never leave this class (see
    /// <see cref="ConsentAcceptValue"/>'s own remarks).</summary>
    private static IReadOnlyList<MessageAction> ConsentActions(string locale) => locale == nameof(Locale.Ru)
        ? [new MessageAction("Согласен(на)", ConsentAcceptValue), new MessageAction("Не согласен(на)", ConsentDeclineValue)]
        : [new MessageAction("Accept", ConsentAcceptValue), new MessageAction("Decline", ConsentDeclineValue)];

    /// <summary>`25-153`: the tenant's own public policy page - `ago-console`'s `/policies/:documentKey`
    /// (`23-37`), the identical route the widget's own consent checkbox already links to
    /// (`ago-widget`'s `ui/contactCapture.ts`, `buildConsentLabel`). Built from
    /// <see cref="OperatorInviteOptions.ConsoleBaseUrl"/> rather than a second, purpose-named config
    /// value: that option is already "the one canonical console origin" this codebase resolves
    /// (`OperatorInviteOptions`'s own remarks reject reusing `Ago.Chat.Api.Cors.ConsoleOriginOptions`
    /// for the identical reason - Application cannot reference `Ago.Chat.Api` at all), and a second key
    /// bound to the identical physical URL would only ever be a config-drift risk with no benefit - see
    /// this item's own report for why that reuse, not a new `ConsentLinkOptions`, was the judgment call
    /// made here.</summary>
    private string ConsentDocumentLink(string documentKey) =>
        $"{consoleOptions.ConsoleBaseUrl.TrimEnd('/')}/policies/{Uri.EscapeDataString(documentKey)}";

    private enum ConsentGateStatus
    {
        /// <summary>Either this site never turned <see cref="WidgetConfig.RequireContactConsent"/> on,
        /// or this visitor's own acceptance is already on file - the phone step proceeds exactly as it
        /// did before this item, `25-153`'s own "completely unaffected" Done-when.</summary>
        Clear,

        /// <summary>Required, unmet, and there is a real document to ask about.</summary>
        Pending,

        /// <summary>Required, unmet, and the tenant has never published a document under this purpose's
        /// own key - <see cref="ConsentUnavailableText"/>'s own remarks.</summary>
        Unavailable,
    }

    private sealed record ConsentGateOutcome(
        ConsentGateStatus Status, string? DocumentKey, string? DocumentVersion, string? Title)
    {
        public static readonly ConsentGateOutcome Clear = new(ConsentGateStatus.Clear, null, null, null);

        public static readonly ConsentGateOutcome Unavailable = new(ConsentGateStatus.Unavailable, null, null, null);

        public static ConsentGateOutcome Pending(string documentKey, string documentVersion, string title) =>
            new(ConsentGateStatus.Pending, documentKey, documentVersion, title);
    }

    /// <summary>
    /// `25-153`: the general, not-calendar-specific half of this item's own design - "is this site's own
    /// PD-consent gate satisfied for this visitor, right now" - re-derived fresh on every call from
    /// existing state, never cached and never stored on the <see cref="Domain.ModuleTask"/> itself (the
    /// backlog item's own "a small, bounded derivation... do not add any new persistent field" Scope).
    ///
    /// <para>The identical read <see cref="RecordVisitorContactDetail.RecordVisitorContactDetailHandler.ConsentSatisfiedAsync"/>
    /// already performs for the widget's own contact-detail write, and the identical fact
    /// `GetConsentRequirementHandler` surfaces to the widget before it ever shows its own checkbox -
    /// three call sites computing the same "required, and satisfied how" question locally rather than
    /// one calling another, matching this codebase's own established shape (<c>ConsentSatisfiedAsync</c>
    /// is itself a private method, not shared with `GetConsentRequirementHandler` even though both read
    /// the identical rows) rather than introducing the first Application-handler-calls-another-handler
    /// dependency in this repository.</para>
    /// </summary>
    private async Task<ConsentGateOutcome> ResolveConsentGateAsync(
        SiteId siteId, VisitorId visitorId, CancellationToken cancellationToken)
    {
        var site = await sites.GetByIdAsync(siteId, cancellationToken);
        if (site is null || !site.WidgetConfig.RequireContactConsent)
        {
            return ConsentGateOutcome.Clear;
        }

        var documentKey = SiteConsentDocumentKey.For(siteId, VisitorConsentPurpose.Contact);
        var accepted = await acceptances.GetForSubjectAsync(AcceptanceSubjectKind.Visitor, visitorId.Value, cancellationToken);
        if (accepted.Any(a => a.DocumentKey == documentKey))
        {
            return ConsentGateOutcome.Clear;
        }

        var current = await documents.FindCurrentAsync(documentKey, cancellationToken);
        return current is null
            ? ConsentGateOutcome.Unavailable
            : ConsentGateOutcome.Pending(documentKey, current.Version, current.Title);
    }

    /// <summary>`25-153`: builds and sends this gate's own <see cref="PrimitiveKinds.ChoiceList"/>
    /// message - the first time a phone-collection step is withheld (<see cref="FinishStepAsync"/>'s own
    /// interception) and every re-offer after a decline (<see cref="ContinueActiveTaskAsync"/>'s own
    /// gate branch). <paramref name="applyBeforeMessage"/> is the one thing that differs between those
    /// two callers: the first also has to record the real, withheld step onto the task
    /// (<see cref="FinishStepAsync"/>'s own remarks on why); a re-offer after decline changes nothing
    /// about the task at all, so that caller passes a no-op.</summary>
    private async Task<Result<RouteConversationToModuleOutcome>> SendConsentPromptAsync(
        Conversation conversation, ConsentGateOutcome gate, string locale, bool explainDecline,
        RouteConversationToModuleOutcome outcome, DateTimeOffset now, RouteConversationToModule command,
        Action<Conversation> applyBeforeMessage, CancellationToken cancellationToken)
    {
        var link = ConsentDocumentLink(gate.DocumentKey!);
        var prompt = ConsentPromptText(locale, gate.Title!, link, explainDecline);
        var payload = new MessagePayload(JsonSerializer.Serialize(new { prompt }));
        var actions = ConsentActions(locale);
        var kind = new MessageContentKind(PrimitiveKinds.ChoiceList);
        var body = PrimitiveTextRenderer.Render(prompt, PrimitiveKinds.ChoiceList, payload, actions, locale);
        var content = MessageContent.Create(kind, payload, actions);
        var messageId = new MessageId(idGenerator.NewId(now));

        return await AddSystemMessageAndSaveAsync(
            conversation, command, outcome,
            c =>
            {
                applyBeforeMessage(c);
                c.AddSystemMessage(messageId, new MessageBody(body), now, content: content);
            },
            cancellationToken);
    }

    /// <summary>`25-153`: the escalation this gate takes when there is nothing to ask about at all -
    /// <see cref="ConsentGateStatus.Unavailable"/>. The identical "task closed, a person takes over"
    /// shape <see cref="ModuleBecameUnreachableText"/>'s own call site already uses for a module gone
    /// unreachable, reused here for a dead end this task's own module never caused and cannot fix.
    ///
    /// <para><paramref name="applyStepBeforeClosing"/> exists only for <see cref="FinishStepAsync"/>'s
    /// own call: reached from <c>TryStartTaskAsync</c>, no <see cref="Domain.ModuleTask"/> exists on
    /// <paramref name="conversation"/> yet, so <see cref="Conversation.CloseModuleTask"/> would throw
    /// <see cref="InvalidConversationStateException"/> unless the task is started first - the identical
    /// step every ungated `moduleSaysComplete`/escalate close already applies before its own
    /// <c>CloseModuleTask</c> call. <see cref="ContinueConsentGateAsync"/>'s own call passes
    /// <see langword="null"/>: that task is already active, so there is already something to
    /// close.</para></summary>
    private async Task<Result<RouteConversationToModuleOutcome>> EscalateForUnavailableConsentAsync(
        Conversation conversation, string locale, DateTimeOffset now, RouteConversationToModule command,
        Action<Conversation>? applyStepBeforeClosing, CancellationToken cancellationToken)
    {
        var messageId = new MessageId(idGenerator.NewId(now));
        return await AddSystemMessageAndSaveAsync(
            conversation, command, RouteConversationToModuleOutcome.Escalated,
            c =>
            {
                applyStepBeforeClosing?.Invoke(c);
                c.CloseModuleTask(now);
                c.AddSystemMessage(messageId, new MessageBody(ConsentUnavailableText(locale)), now, content: null);
            },
            cancellationToken);
    }

    public async Task<Result<RouteConversationToModuleOutcome>> HandleAsync(
        RouteConversationToModule command, CancellationToken cancellationToken)
    {
        // THE LOOP GUARD - the same one `SendOfflineAutoReplyHandler` states first, before any I/O: a
        // module-produced system message must cost this consumer nothing at all, or a reply's own
        // MessageAccepted would be handed straight back in as if the visitor had sent it.
        if (command.TriggerAuthorKind != MessageAuthorKind.Visitor)
        {
            return RouteConversationToModuleOutcome.NotAVisitorMessage;
        }

        var conversation = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        var trigger = conversation.Messages.FirstOrDefault(m => m.Sequence == command.TriggerSequence);
        if (trigger is null || trigger.AuthorKind != MessageAuthorKind.Visitor)
        {
            return RouteConversationToModuleOutcome.NotAVisitorMessage;
        }

        var now = clock.UtcNow;
        var modulesForSite = await moduleReadStore.GetForSiteAsync(command.SiteId, now, cancellationToken);

        return conversation.ActiveModuleTask is { } active
            ? await ContinueActiveTaskAsync(conversation, active, trigger, modulesForSite, now, command, cancellationToken)
            : await TryStartTaskAsync(conversation, trigger, modulesForSite, now, command, cancellationToken);
    }

    private async Task<Result<RouteConversationToModuleOutcome>> TryStartTaskAsync(
        Conversation conversation, Message trigger, IReadOnlyList<EnabledModuleSummary> modulesForSite,
        DateTimeOffset now, RouteConversationToModule command, CancellationToken cancellationToken)
    {
        var candidates = modulesForSite
            .Select(m => new TriggerCommandMatcher.Candidate(m.ModuleKey, m.TriggerWords))
            .ToList();
        var matchedKey = TriggerCommandMatcher.Match(trigger.Body.Value, candidates);
        if (matchedKey is not { } key)
        {
            return RouteConversationToModuleOutcome.NoTriggerMatch;
        }

        var enabledModule = modulesForSite.First(m => m.ModuleKey == key);
        // `25-37`: the site's own configured widget language, resolved once here and handed to the
        // module for its very first step - see ResolveLocaleAsync's own remarks for why a missing site
        // reads as the safe default rather than a hard failure.
        var locale = await ResolveLocaleAsync(command.SiteId, cancellationToken);

        // `25-138`: the server-side name+phone gate, checked before this conversation's own first reply
        // is ever forwarded into a module - the exact entry point the backlog item names. See
        // ResolveContactGateAsync's own remarks for what it checks and why.
        var contactGate = await ResolveContactGateAsync(conversation.VisitorId, cancellationToken);
        if (contactGate != ContactGateStatus.Clear)
        {
            return await OpenContactGateAsync(conversation, trigger, key, contactGate, locale, now, command, cancellationToken);
        }

        return await StartRealModuleTaskAsync(
            conversation, trigger, key, enabledModule, locale, now, command, beforeStart: _ => { }, cancellationToken);
    }

    /// <summary>
    /// `25-138`: the one place this class ever calls <c>gateway.StartTaskAsync</c> - pulled out of
    /// <see cref="TryStartTaskAsync"/> unchanged so <see cref="ContinueContactGateReplyAsync"/> can reach
    /// the identical call once this gate clears, with the visitor's own original trigger (never the
    /// gate's own name/phone replies) as <see cref="StartModuleTaskRequest.TriggerText"/> - the backlog
    /// item's own "forward the original reply to the module exactly as before this item."
    /// <paramref name="beforeStart"/> is the one thing that differs between the two callers: a no-op for
    /// <see cref="TryStartTaskAsync"/> (nothing precedes an ordinary task start), and
    /// <c>c =&gt; c.CloseModuleTask(now)</c> for <see cref="ContinueContactGateReplyAsync"/> (this gate's
    /// own Chat-only task has to close before <see cref="Conversation.StartModuleTask"/> can open the
    /// real one - <see cref="Conversation.ActiveModuleTask"/> allows only one at a time). Folded into the
    /// same replayable <c>applyStep</c> delegate <see cref="FinishStepAsync"/> already builds, for the
    /// identical "a save-retry replays the whole sequence, never half of it" reason that method's own
    /// remarks give.
    /// </summary>
    private async Task<Result<RouteConversationToModuleOutcome>> StartRealModuleTaskAsync(
        Conversation conversation, Message trigger, ModuleKey key, EnabledModuleSummary enabledModule, string locale,
        DateTimeOffset now, RouteConversationToModule command, Action<Conversation> beforeStart,
        CancellationToken cancellationToken)
    {
        // `20-07`'s own id trick: Chat's own ModuleTaskId doubles as the wire contract's `chatTaskId` -
        // the module is handed exactly the id this aggregate will use to identify the task once
        // StartModuleTask below succeeds, so no second id has to be invented or reconciled.
        var chatTaskId = idGenerator.NewId(now);
        StartModuleTaskResult startResult;
        try
        {
            startResult = await gateway.StartTaskAsync(
                new EnabledModuleEndpoint(key, command.SiteId, enabledModule.EntryPoint, enabledModule.Credential),
                // `26-136`/`adr/0184`: chat owns the person and the calendar references it opaquely, so the
                // module call carries this conversation's own visitor (person) id and conversation id. Same
                // "resend on every call, never persisted on the module's side" shape KnownPhone already uses.
                new StartModuleTaskRequest(
                    chatTaskId, command.SiteId, command.ConversationId, trigger.Body.Value, locale,
                    conversation.VisitorId.Value, conversation.Id.Value),
                cancellationToken);
        }
        catch (ModuleUnreachableException)
        {
            // Nothing was ever started domain-side - there is no task to close, only an apology to add
            // (plus, for the gate-cleared caller, closing this gate's own Chat-only task first).
            var messageId = new MessageId(idGenerator.NewId(now));
            return await AddSystemMessageAndSaveAsync(
                conversation, command, RouteConversationToModuleOutcome.ModuleUnavailableAtTrigger,
                c =>
                {
                    beforeStart(c);
                    c.AddSystemMessage(messageId, new MessageBody(ModuleUnavailableText(locale)), now, content: null);
                },
                cancellationToken);
        }

        // `25-34`: StartModuleTask no longer runs here - it moves into FinishStepAsync's own
        // applyStep delegate, so a `ConversationConcurrencyConflictException` on the save below can
        // reapply it (and the message it produces) together, against a freshly reloaded Conversation,
        // rather than leaving it half-done against the stale tracked instance. See
        // AddSystemMessageAndSaveAsync's own remarks for why replaying it is safe.
        //
        // A first step is always reported as `TaskStarted`, regardless of `startResult.Complete` - the
        // enum's own doc comment ("a new ModuleTask is now the conversation's active one") describes the
        // task's birth, not its length, and a single-round-trip module (`startResult.Complete == true`
        // on the very first answer) is not a distinct case a caller needs to tell apart from a
        // multi-step one. Escalation is the one exception: it is not "the task started", it is "the task
        // started and immediately had to be handed off", which is why it still gets its own outcome.
        return await FinishStepAsync(
            conversation, trigger, startResult.Step, startResult.Complete, RouteConversationToModuleOutcome.TaskStarted,
            now, command, locale,
            c =>
            {
                beforeStart(c);
                c.StartModuleTask(
                    new ModuleTaskId(chatTaskId), key, startResult.ExternalTaskId, now,
                    startResult.Step.Kind, startResult.Step.Payload, startResult.Step.Actions);
            },
            cancellationToken);
    }

    private enum ContactGateStatus
    {
        /// <summary>Either this visitor did not arrive through a gated channel (no <see cref="ChannelIdentity"/>
        /// at all - a widget visitor, `25-136`'s own client-side gate already covers it - or one whose
        /// <see cref="ChannelIdentity.Kind"/> is not explicitly named below), or both a name and a phone
        /// are already on file for them.</summary>
        Clear,

        /// <summary>A gated channel, and no <see cref="VisitorContactDetailKind.Phone"/> on file yet -
        /// checked, and asked for, before <see cref="NeedsName"/> (see <see cref="ResolveContactGateAsync"/>'s
        /// own remarks for why).</summary>
        NeedsPhone,

        /// <summary>A gated channel, a phone already on file, but no <see cref="VisitorContactDetailKind.Name"/>
        /// yet.</summary>
        NeedsName,
    }

    /// <summary>
    /// `25-138`: the server-side equivalent of `25-136`'s widget-only, client-side contact gate - derived
    /// fresh from existing state on every call, exactly the way `25-153`'s own <see cref="ResolveConsentGateAsync"/>
    /// is (never persisted onto the <see cref="Domain.ModuleTask"/> itself; there is no new column
    /// anywhere in this item).
    ///
    /// <para><b>Gated on <see cref="ChannelKind"/>, explicitly - never on "not the widget."</b> A widget
    /// visitor never links a <see cref="ChannelIdentity"/> at all (that type's own remarks: "one built-in
    /// identity mechanism, plus N external ones that link into it"), so <paramref name="visitorId"/>
    /// resolving no identity here already excludes the widget structurally; the explicit
    /// <c>Telegram</c>/<c>Max</c> check on top is what keeps a future channel with its own native capture
    /// mechanism out of this gate by name, deliberately, rather than by the accident of merely not being
    /// the widget - this backlog item's own "where this is likely to go wrong" warning.</para>
    ///
    /// <para><b>Phone before name.</b> Asking for the phone first means this gate's own first step is
    /// always <see cref="PrimitiveKinds.IsPhoneCollectionStep"/>-shaped, which is what lets `25-153`'s own
    /// consent gate apply to it automatically, with no change to that gate at all (see
    /// <see cref="OpenContactGateAsync"/>'s own remarks) - and it is also what makes
    /// <see cref="RecordVisitorContactDetailHandler.HandleAsVisitorAsync"/>'s own <em>unconditional</em>
    /// consent check (it gates every kind, not only <see cref="VisitorContactDetailKind.Phone"/>) a
    /// non-issue for the name write that follows: by the time this gate ever asks for a name, a required
    /// consent has already been granted recording the phone.</para>
    /// </summary>
    private async Task<ContactGateStatus> ResolveContactGateAsync(VisitorId visitorId, CancellationToken cancellationToken)
    {
        var identity = await channelIdentities.FindMostRecentForVisitorAsync(visitorId, cancellationToken);
        if (identity is not { Kind: ChannelKind.Telegram or ChannelKind.Max })
        {
            return ContactGateStatus.Clear;
        }

        var details = await contactDetails.GetForVisitorAsync(visitorId, cancellationToken);
        if (!details.Any(d => d.Kind == VisitorContactDetailKind.Phone))
        {
            return ContactGateStatus.NeedsPhone;
        }

        return details.Any(d => d.Kind == VisitorContactDetailKind.Name) ? ContactGateStatus.Clear : ContactGateStatus.NeedsName;
    }

    private static string ContactGatePhonePromptText(string locale) => locale == nameof(Locale.Ru)
        ? "Прежде чем продолжить, оставьте, пожалуйста, номер телефона для связи."
        : "Before we continue, please share a phone number we can reach you on.";

    private static string ContactGateNamePromptText(string locale) => locale == nameof(Locale.Ru)
        ? "И как к вам обращаться?"
        : "And what name should we use for you?";

    /// <summary>
    /// `25-138`: builds whichever of this gate's own two <see cref="PrimitiveKinds.Form"/> steps
    /// <paramref name="status"/> says is still owed - never a new wire shape (the backlog item's own "no
    /// new wire shape" Scope), just this vocabulary's existing single-field form, wire-shaped exactly
    /// like `Ago.Calendar`'s own <c>ModuleStepFactory.PhoneForm</c>. The phone step's own <c>fieldId</c>
    /// is the unmodified <c>"phone"</c> this vocabulary already reserves - <see
    /// cref="PrimitiveKinds.IsPhoneCollectionStep"/>'s exact condition - so `25-153`'s own consent gate
    /// recognises it automatically the moment <see cref="FinishStepAsync"/> is asked to show it, with no
    /// change to that gate at all.
    /// </summary>
    private static ModuleStep BuildContactGateStep(ContactGateStatus status, int originalTriggerSequence, string locale)
    {
        var (fieldId, label, prompt) = status == ContactGateStatus.NeedsPhone
            ? ("phone", locale == nameof(Locale.Ru) ? "Телефон" : "Phone", ContactGatePhonePromptText(locale))
            : ("name", locale == nameof(Locale.Ru) ? "Имя" : "Name", ContactGateNamePromptText(locale));

        var payload = new MessagePayload(JsonSerializer.Serialize(new
        {
            prompt,
            fieldId,
            fieldLabel = label,
            _gateTriggerSequence = originalTriggerSequence,
        }));

        return new ModuleStep(new MessageContentKind(PrimitiveKinds.Form), payload, []);
    }

    /// <summary>See <see cref="ContactGateTriggerSequenceField"/>'s own remarks.</summary>
    private static int ReadGateTriggerSequence(MessagePayload? payload)
    {
        using var document = JsonDocument.Parse(payload!.Value.Value);
        return document.RootElement.GetProperty(ContactGateTriggerSequenceField).GetInt32();
    }

    /// <summary>
    /// `25-138`: opens this gate - reached only from <see cref="TryStartTaskAsync"/>, the one entry point
    /// the backlog item names ("the conversation's first reply into a module"). Routed through
    /// <see cref="FinishStepAsync"/> unmodified, exactly the way `25-153`'s own consent gate already
    /// routes a module's real phone step through it: <paramref name="status"/>'s own phone-shaped step
    /// (see <see cref="ResolveContactGateAsync"/>'s own "phone before name" remarks) is what lets that
    /// consent gate intercept it automatically, with nothing here or in <see cref="FinishStepAsync"/>
    /// needing to know this gate exists.
    /// </summary>
    private async Task<Result<RouteConversationToModuleOutcome>> OpenContactGateAsync(
        Conversation conversation, Message trigger, ModuleKey key, ContactGateStatus status, string locale,
        DateTimeOffset now, RouteConversationToModule command, CancellationToken cancellationToken)
    {
        var chatTaskId = new ModuleTaskId(idGenerator.NewId(now));
        var step = BuildContactGateStep(status, trigger.Sequence, locale);
        return await FinishStepAsync(
            conversation, trigger, step, moduleSaysComplete: false, RouteConversationToModuleOutcome.TaskStarted,
            now, command, locale,
            c => c.StartModuleTask(chatTaskId, key, ContactGateExternalTaskId, now, step.Kind, step.Payload, step.Actions),
            cancellationToken);
    }

    /// <summary>
    /// `25-138`: a reply against this gate's own active (Chat-only) task - reached from
    /// <see cref="ContinueActiveTaskAsync"/>'s own <see cref="ContactGateExternalTaskId"/> check, itself
    /// placed <em>after</em> that method's existing `25-153` consent-gate branch runs unmodified (a reply
    /// answering that gate's own consent choice must resolve there first, never here).
    ///
    /// <para>Records the reply through the identical, consent-gate-aware, rate-limited write path every
    /// other source of a <see cref="Domain.VisitorContactDetail"/> already goes through - never a second
    /// one (the backlog item's own explicit instruction). Recorded before this reply's own dedup/save
    /// point below, the same accepted at-least-once cost `ContinueConsentGateAsync`'s own remarks already
    /// state for its acceptance write: a redelivered reply would record a second, harmless row (<see
    /// cref="Domain.VisitorContactDetail"/> carries no per-kind uniqueness - that type's own remarks), not
    /// a wrong outcome.</para>
    /// </summary>
    private async Task<Result<RouteConversationToModuleOutcome>> ContinueContactGateReplyAsync(
        Conversation conversation, ModuleTask active, Message trigger, IReadOnlyList<EnabledModuleSummary> modulesForSite,
        string locale, DateTimeOffset now, RouteConversationToModule command, CancellationToken cancellationToken)
    {
        var isPhoneStage = PrimitiveKinds.IsPhoneCollectionStep(active.LastStepKind!.Value.Value, active.LastStepPayload);
        var kind = isPhoneStage ? nameof(VisitorContactDetailKind.Phone) : nameof(VisitorContactDetailKind.Name);

        var written = await recordContactDetail.HandleAsVisitorAsync(
            new RecordVisitorContactDetailAsVisitor(conversation.Id, conversation.VisitorId, kind, trigger.Body.Value),
            cancellationToken);
        if (written.IsFailure)
        {
            // Empty/oversized text, or a rate limit - the same "malformed reply, task stays open, nothing
            // saved" outcome every other unresolved reply in this class already gets (ResolveReplyValue's
            // own null branch); the visitor can simply answer again.
            return RouteConversationToModuleOutcome.ReplyNotResolved;
        }

        var originalTriggerSequence = ReadGateTriggerSequence(active.LastStepPayload);
        var gate = await ResolveContactGateAsync(conversation.VisitorId, cancellationToken);
        if (gate != ContactGateStatus.Clear)
        {
            // The phone was just given, the name is still owed (or vice versa, for a visitor who already
            // had a phone on file but no name) - advance this gate's own Chat-only task to its next step,
            // exactly the way a real module's own multi-step task advances.
            var nextStep = BuildContactGateStep(gate, originalTriggerSequence, locale);
            return await FinishStepAsync(
                conversation, trigger, nextStep, moduleSaysComplete: false, RouteConversationToModuleOutcome.StepAdvanced,
                now, command, locale, c => c.RecordModuleStep(nextStep.Kind, nextStep.Payload, nextStep.Actions),
                cancellationToken);
        }

        var enabledModule = modulesForSite.FirstOrDefault(m => m.ModuleKey == active.ModuleKey);
        if (enabledModule is null)
        {
            // The module was disabled while this visitor was going through this gate - indistinguishable
            // from the module having gone unreachable, the identical posture ContinueActiveTaskAsync's
            // own top already takes for a real module task in the same situation.
            var messageId = new MessageId(idGenerator.NewId(now));
            return await AddSystemMessageAndSaveAsync(
                conversation, command, RouteConversationToModuleOutcome.Escalated,
                c =>
                {
                    c.CloseModuleTask(now);
                    c.AddSystemMessage(messageId, new MessageBody(ModuleBecameUnreachableText(locale)), now, content: null);
                },
                cancellationToken);
        }

        // Both on file now - this Chat-only gate task closes, and the visitor's own original reply
        // (never itself shown to, or consumed by, this gate) finally reaches the module, exactly as it
        // would have before this item existed.
        var originalTrigger = conversation.Messages.First(m => m.Sequence == originalTriggerSequence);
        return await StartRealModuleTaskAsync(
            conversation, originalTrigger, active.ModuleKey, enabledModule, locale, now, command,
            beforeStart: c => c.CloseModuleTask(now), cancellationToken);
    }

    private async Task<Result<RouteConversationToModuleOutcome>> ContinueActiveTaskAsync(
        Conversation conversation, ModuleTask active, Message trigger, IReadOnlyList<EnabledModuleSummary> modulesForSite,
        DateTimeOffset now, RouteConversationToModule command, CancellationToken cancellationToken)
    {
        // `25-37`/`25-38`/`25-39`: three more facts a module may need to answer this reply, resolved
        // fresh on every call rather than remembered anywhere - see SubmitModuleReplyRequest.Locale's
        // own remarks for why this item spends no migration on persisting them. Read unconditionally
        // (not gated to a particular step kind): none of the three reads is expensive - a single-row
        // Site lookup and a visitor's own small, bounded contact-detail list
        // (VisitorContactDetail's own remarks) - and gating on step kind would make this handler's own
        // behaviour depend on knowledge of which primitive kind a booking module happens to use its
        // phone step for, which is exactly the "no special-casing per primitive kind" constraint this
        // handler's own type remarks already hold ResolveReplyValue to.
        //
        // `25-64`: moved ahead of the `VerifiedPhoneForm` gate below, deliberately - `acceptUnverifiedPhone`
        // is what that gate now reads before it can refuse a reply, so it has to exist before the gate
        // runs rather than after it.
        //
        // `25-66`: moved ahead of the `enabledModule` check right below, a second time - `locale` is now
        // also what `ModuleBecameUnreachableText` reads for that branch's own refusal, so it has to exist
        // before that check can return early, not only before the phone gate further down. The one cost
        // this second move accepts: the `ReplyNotResolved` early return just past the `enabledModule`
        // check now also pays for this read, where before `25-66` it did not - an ordinary Site lookup,
        // not a new kind of read, and the identical trade `PhoneVerificationRequiredText`'s own
        // resolution already made once, `25-64`'s own remarks record it there.
        var (locale, acceptUnverifiedPhone) = await ResolveModuleContextAsync(conversation.SiteId, cancellationToken);

        var enabledModule = modulesForSite.FirstOrDefault(m => m.ModuleKey == active.ModuleKey);
        if (enabledModule is null)
        {
            // The module was disabled while this task was open - indistinguishable, from the
            // conversation's point of view, from the module having gone unreachable: either way, input
            // has nowhere to go, and the same escalation applies. `25-34`: CloseModuleTask moves into
            // the applyMutations delegate below rather than running here directly, so a retry against a
            // freshly reloaded Conversation reapplies it too - see AddSystemMessageAndSaveAsync's own
            // remarks.
            var messageId = new MessageId(idGenerator.NewId(now));
            return await AddSystemMessageAndSaveAsync(
                conversation, command, RouteConversationToModuleOutcome.Escalated,
                c =>
                {
                    c.CloseModuleTask(now);
                    c.AddSystemMessage(messageId, new MessageBody(ModuleBecameUnreachableText(locale)), now, content: null);
                },
                cancellationToken);
        }

        // `25-153`: the general consent gate, re-checked before ResolveReplyValue rather than trusted
        // from whenever this task's own phone-collection step was first shown. If the step this task is
        // currently waiting a reply to is this vocabulary's own phone-collection shape, and this site's
        // own gate is not yet satisfied for this visitor, a real phone number was never actually
        // rendered - FinishStepAsync would have substituted this gate's own consent choice for it - so
        // this reply can only ever be answering *that* choice. Nothing about this is remembered on
        // `active` itself: it is derived fresh, every time, from the identical two facts
        // ResolveConsentGateAsync always reads - the backlog item's own "derive it, don't persist it"
        // instruction, applied to the reply side the same way FinishStepAsync already applies it to the
        // send side.
        if (active.LastStepKind is { } activeStepKind
            && PrimitiveKinds.IsPhoneCollectionStep(activeStepKind.Value, active.LastStepPayload))
        {
            var gate = await ResolveConsentGateAsync(conversation.SiteId, conversation.VisitorId, cancellationToken);
            if (gate.Status != ConsentGateStatus.Clear)
            {
                return await ContinueConsentGateAsync(conversation, active, trigger, gate, locale, now, command, cancellationToken);
            }
        }

        // `25-138`: the active task is this gate's own Chat-only task, not a real module's - see
        // ContactGateExternalTaskId's own remarks for why this sentinel, rather than the step kind, is
        // the discriminator. Placed after the `25-153` consent-gate branch above runs unmodified: a reply
        // still answering that gate's own consent choice must resolve there first, never here.
        if (active.ExternalTaskId == ContactGateExternalTaskId)
        {
            return await ContinueContactGateReplyAsync(
                conversation, active, trigger, modulesForSite, locale, now, command, cancellationToken);
        }

        var value = ResolveReplyValue(trigger, active);
        if (value is null)
        {
            // Could not resolve - an out-of-range or non-numeric text-channel reply, or a widget reply
            // whose payload carried no usable value. The module is never called, and the task stays
            // open exactly as it was: no domain event, nothing to stage, nothing to save.
            return RouteConversationToModuleOutcome.ReplyNotResolved;
        }

        var knownPhone = await ResolveKnownPhoneAsync(conversation.VisitorId, cancellationToken);

        // `20-09`/`25-64`: the structural gate. A reply against a step this vocabulary marks as needing
        // a *verified* phone is checked against `14-15`'s own evidence - an active ChannelIdentity for
        // this exact (site, Sms, phone) triple, owned by this conversation's own visitor - before the
        // module is ever called, the identical "recognise the kind by value, act before forwarding"
        // shape `PrimitiveKinds.Escalate`'s own handling already established (`adr/0081`). Calendar
        // never sees this decision when it is Chat's to make; it only ever sees the result (a
        // timestamp, or no reply at all).
        //
        // `adr/0163`: with the site's own `AcceptUnverifiedPhone` on, this gate is no longer Chat's
        // decision to make alone - the ADR's own text is explicit that "the phone step still renders,
        // but without demanding proof of control over the number," which only holds if Chat actually
        // forwards an unverified reply rather than refusing it here first. Before this item, the gate
        // refused unconditionally, so `AcceptUnverifiedPhone` could never take effect for a phone Chat
        // had not already verified - the exact gap `25-64` closes. `RequiresVerifiedPhone =
        // !acceptUnverifiedPhone` (`BookEventHandler`, `ago-calendar`) is where the setting actually
        // takes its effect; this gate now only stops an unverified reply when the tenant has not opted
        // in, which is the unchanged default behaviour every tenant already had.
        //
        // A phone that does not even parse (not this vocabulary's concern - shape validation is
        // Calendar's own `PhoneNumber`, the same "Chat never re-validates what a module already
        // validates" split `ReplyToModuleTaskHandler`'s own remarks describe for a reply's id) is
        // forwarded unchanged either way, exactly as an ordinary `form` reply always has been:
        // Calendar's own `BookEventHandler` turns it into `booking.invalid_phone`, and the existing
        // lost-race re-offer path handles it from there - no second validation invented here.
        DateTimeOffset? phoneVerifiedAt = null;
        if (active.LastStepKind!.Value.Value == PrimitiveKinds.VerifiedPhoneForm)
        {
            PhoneNumber? phone = null;
            try
            {
                phone = new PhoneNumber(value);
            }
            catch (ArgumentException)
            {
                // Malformed - not this handler's concern, see the remarks above. Falls through with
                // phoneVerifiedAt left null; Calendar rejects the shape itself.
            }

            if (phone is { } parsedPhone)
            {
                var address = new ExternalChannelAddress(parsedPhone.Value);
                var identity = await channelIdentities.FindAsync(
                    conversation.SiteId, ChannelKind.Sms, address, cancellationToken);

                if (identity is null || identity.VisitorId != conversation.VisitorId)
                {
                    if (!acceptUnverifiedPhone)
                    {
                        // Not verified for this visitor, and this tenant has not opted into skipping
                        // that guarantee - never forwarded. The task stays open (the visitor can retype
                        // the identical number once verification actually completes, through `14-15`'s
                        // own endpoints - there is no widget popup wired to trigger them yet, `20-09`'s
                        // own report names this as the deferred, frontend-side follow-up).
                        var messageId = new MessageId(idGenerator.NewId(now));
                        return await AddSystemMessageAndSaveAsync(
                            conversation, command, RouteConversationToModuleOutcome.PhoneVerificationRequired,
                            c => c.AddSystemMessage(
                                messageId, new MessageBody(PhoneVerificationRequiredText(locale)), now, content: null),
                            cancellationToken);
                    }

                    // `25-64`/`adr/0163`: opted in, and still not verified - falls through with
                    // `phoneVerifiedAt` left null, exactly as the malformed-phone case above already
                    // does, so this reply reaches `gateway.SubmitReplyAsync` below unchanged. Calendar's
                    // own `RequiresVerifiedPhone = !acceptUnverifiedPhone` is what actually decides
                    // whether the booking completes from here - Chat's job for this tenant is only to
                    // stop pretending a self-reported number is proven, never to block it outright.
                }
                else
                {
                    // Verified - and has been since `identity.FirstSeenAt` (`ChannelIdentity.Link`'s own
                    // instant, never touched again by `Touch`), which is the honest answer to "since
                    // when" rather than "now": Calendar snapshots this value verbatim (`20-09`'s own
                    // "cross-product data question" - Chat asserts, Calendar trusts, the identical
                    // `adr/0077` boundary `20-07`'s module-task endpoints already accept).
                    phoneVerifiedAt = identity.FirstSeenAt;
                }
            }
        }

        SubmitModuleReplyResult replyResult;
        try
        {
            replyResult = await gateway.SubmitReplyAsync(
                new EnabledModuleEndpoint(active.ModuleKey, conversation.SiteId, enabledModule.EntryPoint, enabledModule.Credential),
                new SubmitModuleReplyRequest(
                    active.ExternalTaskId, active.Id.Value, active.LastStepKind!.Value, value, phoneVerifiedAt,
                    locale, knownPhone, acceptUnverifiedPhone,
                    // `26-136`/`adr/0184`: the reply is the call a booking is actually written on, so this
                    // conversation's own person id and conversation id ride it - threaded onto the booked
                    // calendar Event. See StartModuleTaskRequest above for the ownership reasoning.
                    conversation.VisitorId.Value, conversation.Id.Value),
                cancellationToken);
        }
        catch (ModuleUnreachableException)
        {
            var messageId = new MessageId(idGenerator.NewId(now));
            return await AddSystemMessageAndSaveAsync(
                conversation, command, RouteConversationToModuleOutcome.Escalated,
                c =>
                {
                    c.CloseModuleTask(now);
                    c.AddSystemMessage(messageId, new MessageBody(ModuleBecameUnreachableText(locale)), now, content: null);
                },
                cancellationToken);
        }

        if (replyResult.Step is { } step)
        {
            var nonEscalationOutcome = replyResult.Complete
                ? RouteConversationToModuleOutcome.TaskCompleted
                : RouteConversationToModuleOutcome.StepAdvanced;
            return await FinishStepAsync(
                conversation, trigger, step, replyResult.Complete, nonEscalationOutcome, now, command, locale,
                c => c.RecordModuleStep(step.Kind, step.Payload, step.Actions),
                cancellationToken);
        }

        // No further step: the module's own "done" with nothing to add - unaffected by `19-03`, since
        // an escalate step always carries a step (that is the whole signal); a module that wants to hand
        // off with literally nothing to say still has to say so through a step, not through silence.
        // `25-134`: localized through the same PrimitiveTextRenderer.Strings table this class's own
        // ModuleUnavailableText/ModuleBecameUnreachableText/PhoneVerificationRequiredText/
        // ModuleEscalatedFallbackText siblings hand-write their own pairs for - this one reuses Chat's
        // own primitive-vocabulary table instead of a fifth private method here, because "done, nothing
        // more to say" is knowledge of the module-task lifecycle PrimitiveTextRenderer already owns
        // (it is the text a module's own missing final step falls back to), not a fact specific to this
        // handler the way the other four apology texts are.
        var doneMessageId = new MessageId(idGenerator.NewId(now));
        return await AddSystemMessageAndSaveAsync(
            conversation, command, RouteConversationToModuleOutcome.TaskCompleted,
            c =>
            {
                c.CloseModuleTask(now);
                c.AddSystemMessage(
                    doneMessageId, new MessageBody(PrimitiveTextRenderer.Strings.For(locale).ModuleTaskDone), now,
                    content: null);
            },
            cancellationToken);
    }

    /// <summary>
    /// `25-153`: resolves a reply against this gate's own consent step, exactly the way
    /// <c>ContinueActiveTaskAsync</c>'s own gate branch found it pending. The module is never called from
    /// here, on any path - accept reveals a step the module already sent once (<see cref="FinishStepAsync"/>'s
    /// own remarks on why nothing more is owed to it); decline and "unresolved" both leave the task
    /// exactly where it was.
    /// </summary>
    private async Task<Result<RouteConversationToModuleOutcome>> ContinueConsentGateAsync(
        Conversation conversation, ModuleTask active, Message trigger, ConsentGateOutcome gate, string locale,
        DateTimeOffset now, RouteConversationToModule command, CancellationToken cancellationToken)
    {
        if (gate.Status == ConsentGateStatus.Unavailable)
        {
            // Became unavailable between this task's own phone step and this reply (the tenant's
            // document was unpublished mid-task) - the identical dead end FinishStepAsync's own
            // Unavailable branch already escalates for. `active` is already this conversation's own
            // ActiveModuleTask (that is how ContinueConsentGateAsync was ever reached), so there is
            // already something for CloseModuleTask to close - no applyStepBeforeClosing needed.
            return await EscalateForUnavailableConsentAsync(
                conversation, locale, now, command, applyStepBeforeClosing: null, cancellationToken);
        }

        // `25-159`: structured-first, the same way ResolveReplyValue already does for every ordinary
        // step - but keyed to this gate's own actual wire kind (PrimitiveKinds.ChoiceList) rather than
        // `active.LastStepKind`, which `FinishStepAsync` deliberately overwrites with the real, hidden
        // phone step while this gate's own choice is what the visitor actually sees. A widget reply's
        // `Body` is the clicked button's own display text ("Согласен(на)"/"Не согласен(на)"), never a
        // bare number, so falling through to ChoiceReplyTextResolver unconditionally (the pre-fix
        // shape) silently failed to resolve every widget click - see this method's own remarks and the
        // backlog item's root cause writeup.
        var resolved = trigger.Content is { } structured && structured.Kind.Value == PrimitiveKinds.ChoiceList
            ? TryReadReplyValue(structured.Payload)
            : ChoiceReplyTextResolver.Resolve(trigger.Body.Value, ConsentActions(locale));
        if (resolved is null)
        {
            // Out-of-range or non-numeric - the identical "module never called, task stays open" shape
            // ContinueActiveTaskAsync's own ResolveReplyValue branch already gives every other
            // unresolved reply.
            return RouteConversationToModuleOutcome.ReplyNotResolved;
        }

        if (resolved == ConsentDeclineValue)
        {
            return await SendConsentPromptAsync(
                conversation, gate, locale, explainDecline: true, RouteConversationToModuleOutcome.ConsentDeclined,
                now, command, applyBeforeMessage: _ => { }, cancellationToken);
        }

        // Accept: record the identical acceptance fact `24-01`'s own mechanism writes for the widget
        // (`RecordVisitorConsentHandler`'s own write, reused here as the same domain factory plus port
        // rather than a call across to that handler - see ResolveConsentGateAsync's own remarks on why
        // this class computes the read side locally instead of calling GetConsentRequirementHandler, the
        // identical reasoning applied to the write side), then reveal the real step this task already
        // recorded - see FinishStepAsync's own remarks for why it is already sitting on `active`, unshown,
        // needing no second call to the module to show now.
        //
        // Recorded before this reply's own dedup/save point (`AddSystemMessageAndSaveAsync`'s own
        // ordering), the same accepted at-least-once cost this class's own type remarks already state for
        // the module gateway call - a redelivered accept would write a second acceptance row, which
        // `AcceptanceRecord`'s own remarks already treat as harmless (never an update, never read as
        // "more accepted than a single row would mean").
        var acceptance = AcceptanceRecord.ForVisitor(
            new AcceptanceRecordId(idGenerator.NewId(now)), conversation.VisitorId, gate.DocumentKey!,
            gate.DocumentVersion!, now);
        await acceptances.SaveAsync(acceptance, cancellationToken);

        var revealedKind = active.LastStepKind!.Value;
        var revealedBody = PrimitiveTextRenderer.Render(
            trigger.Body.Value, revealedKind.Value, active.LastStepPayload, active.LastStepActions, locale);
        var revealedContent = MessageContent.Create(revealedKind, active.LastStepPayload, active.LastStepActions);
        var revealedMessageId = new MessageId(idGenerator.NewId(now));

        return await AddSystemMessageAndSaveAsync(
            conversation, command, RouteConversationToModuleOutcome.ConsentGranted,
            c => c.AddSystemMessage(revealedMessageId, new MessageBody(revealedBody), now, content: revealedContent),
            cancellationToken);
    }

    /// <summary>
    /// `19-03`: the one place both call paths (a task's first step and every step after it) decide
    /// what a step means for the task's own lifecycle and which system message to add - pulled out once
    /// <see cref="PrimitiveKinds.Escalate"/> gave the two call sites a second outcome to agree on
    /// identically, rather than duplicating the branch in both.
    ///
    /// <para><b>Escalate force-closes regardless of <paramref name="moduleSaysComplete"/>.</b>
    /// `adr/0065` decision 7's "an escape... cannot be suppressed by the module" was written for the
    /// *unreachable* case, where Chat itself decides to close (the module never gets a vote). An escalate
    /// step is the *reachable* mirror of that same principle: the module is answering, so it could in
    /// principle send <c>escalate</c> with <c>complete: false</c> - by a bug or by a future module's
    /// misuse - and ask to keep the task open anyway. Honouring that would let a module suppress its own
    /// escalation, which is exactly what decision 7 forbids; forcing the close here regardless keeps the
    /// guarantee unconditional rather than "unconditional unless the module says otherwise."</para>
    ///
    /// <para><b>`25-34`: <paramref name="applyStep"/></b> is the one caller-supplied mutation this
    /// method does not decide for itself - <see cref="Conversation.StartModuleTask"/> (a task's first
    /// step) or <see cref="Conversation.RecordModuleStep"/> (every step after it), already bound to its
    /// own call's data by the caller. It is composed into the same replayable delegate this method
    /// builds for its own <see cref="Conversation.CloseModuleTask"/>/<see cref="Conversation.AddSystemMessage"/>
    /// work, so a save that loses the conversation's own optimistic-concurrency check can retry the
    /// *whole* sequence - task mutation, close, and message - against a freshly reloaded aggregate in
    /// one replay, never just part of it. See <see cref="AddSystemMessageAndSaveAsync"/>'s own remarks
    /// for why replaying is safe here (nothing in <paramref name="applyStep"/> or the rest of this
    /// delegate re-derives anything from the module's own already-received answer - that call already
    /// happened, once, before this method was ever reached).</para>
    /// </summary>
    private async Task<Result<RouteConversationToModuleOutcome>> FinishStepAsync(
        Conversation conversation, Message trigger, ModuleStep step, bool moduleSaysComplete,
        RouteConversationToModuleOutcome nonEscalationOutcome, DateTimeOffset now, RouteConversationToModule command,
        string locale, Action<Conversation> applyStep, CancellationToken cancellationToken)
    {
        // `25-153`: before this step is ever shown, ask whether it is this vocabulary's own
        // phone-collection shape and, if so, whether this site's own PD-consent gate is still unmet for
        // this visitor. General on purpose - it inspects the step Chat is about to show, never which
        // module produced it, so a future non-calendar module's own phone step is gated identically with
        // no changes here.
        if (PrimitiveKinds.IsPhoneCollectionStep(step.Kind.Value, step.Payload))
        {
            var gate = await ResolveConsentGateAsync(conversation.SiteId, conversation.VisitorId, cancellationToken);
            if (gate.Status == ConsentGateStatus.Unavailable)
            {
                // `applyStep` still has to run before CloseModuleTask - reached from TryStartTaskAsync,
                // this task does not exist on `conversation` yet at all, so closing one that was never
                // started would throw InvalidConversationStateException; reached from
                // ContinueActiveTaskAsync, `applyStep` is RecordModuleStep against the task that is
                // already open. Either way this is the identical "apply the already-decided mutation,
                // then close" order every ungated close in this method already uses.
                return await EscalateForUnavailableConsentAsync(conversation, locale, now, command, applyStep, cancellationToken);
            }

            if (gate.Status == ConsentGateStatus.Pending)
            {
                // `step.Kind.Value` is a phone-collection kind here (the branch above already asked),
                // never PrimitiveKinds.Escalate - so this task only ever closes below if the module
                // itself said `complete`, the same condition FinishStepAsync's own ungated path applies.
                return await SendConsentPromptAsync(
                    conversation, gate, locale, explainDecline: false, nonEscalationOutcome, now, command,
                    c =>
                    {
                        // The real step is recorded exactly as an ungated task would record it - only the
                        // *rendered message* differs. Recording it now, unshown, is what lets an eventual
                        // accept reveal this same, already-decided step without a second call to the
                        // module - see ContinueActiveTaskAsync's own gate branch.
                        applyStep(c);
                        if (moduleSaysComplete)
                        {
                            c.CloseModuleTask(now);
                        }
                    },
                    cancellationToken);
            }
        }

        var isEscalation = step.Kind.Value == PrimitiveKinds.Escalate;
        // `25-66`: `locale` - already resolved once by each of this method's two callers
        // (`TryStartTaskAsync`'s own `ResolveLocaleAsync`, `ContinueActiveTaskAsync`'s own
        // `ResolveModuleContextAsync`) - reaches `ModuleEscalatedFallbackText` only here, the one place
        // both call paths converge, rather than each caller localizing its own copy of this fallback.
        var fallback = isEscalation ? ModuleEscalatedFallbackText(locale) : trigger.Body.Value;
        var body = PrimitiveTextRenderer.Render(fallback, step.Kind.Value, step.Payload, step.Actions, locale);
        var content = MessageContent.Create(step.Kind, step.Payload, step.Actions);
        var outcome = isEscalation ? RouteConversationToModuleOutcome.Escalated : nonEscalationOutcome;
        var messageId = new MessageId(idGenerator.NewId(now));

        return await AddSystemMessageAndSaveAsync(
            conversation, command, outcome,
            c =>
            {
                applyStep(c);
                if (moduleSaysComplete || isEscalation)
                {
                    c.CloseModuleTask(now);
                }

                c.AddSystemMessage(messageId, new MessageBody(body), now, content: content);
            },
            cancellationToken);
    }

    /// <summary>
    /// The one central, generic reply-parsing function this handler ever calls - the item's own explicit
    /// constraint that it must not special-case per primitive kind. Two channels, one resolution:
    /// <list type="bullet">
    /// <item>A structured reply (a widget click, or any channel that already produced one) carries its
    /// own <see cref="MessageContent"/> whose <see cref="MessageContentKind"/> echoes the step it
    /// answers - its <see cref="MessagePayload"/> is read for the one field this vocabulary's own reply
    /// shape defines (<c>"value"</c>), the identical "Chat owns this primitive's shape, not its
    /// meaning" reasoning <see cref="PrimitiveTextRenderer"/>'s own remarks give for <c>"prompt"</c>.</item>
    /// <item>Anything else is plain text - a text-channel numeric reply, resolved against the last step's
    /// own actions when that step was choice-shaped, or taken verbatim when it was a <see
    /// cref="PrimitiveKinds.Form"/>.</item>
    /// </list>
    /// </summary>
    private static string? ResolveReplyValue(Message trigger, ModuleTask active)
    {
        var lastKind = active.LastStepKind;

        if (trigger.Content is { } structured && lastKind is { } k && structured.Kind.Value == k.Value)
        {
            return TryReadReplyValue(structured.Payload);
        }

        if (lastKind is { } kind && PrimitiveKinds.IsChoiceShaped(kind.Value))
        {
            return ChoiceReplyTextResolver.Resolve(trigger.Body.Value, active.LastStepActions);
        }

        return trigger.Body.Value;
    }

    /// <summary>The reply shape's own one field - see <see cref="ResolveReplyValue"/>'s remarks.</summary>
    private static string? TryReadReplyValue(MessagePayload? payload)
    {
        if (payload is not { } value)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(value.Value);
            return document.RootElement.TryGetProperty("value", out var element)
                && element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>`25-37`: the site's own configured widget language, as the Domain enum's PascalCase
    /// member name - the wire convention <see cref="StartModuleTaskRequest.Locale"/>'s own remarks
    /// name. A site that no longer resolves (deleted between the trigger's own dedup check and this
    /// read - a genuinely narrow window, not the ordinary case) reads as <see cref="Locale.En"/>, the
    /// same safe-default posture <see cref="Site.Locale"/>'s own remarks already take for a row that
    /// predates the column entirely, rather than failing a reply this handler can otherwise still
    /// serve.</summary>
    private async Task<string> ResolveLocaleAsync(SiteId siteId, CancellationToken cancellationToken)
    {
        var site = await sites.GetByIdAsync(siteId, cancellationToken);
        return (site?.Locale ?? Locale.En).ToString();
    }

    /// <summary>`25-37`/`25-39`: the one Site read <see cref="ContinueActiveTaskAsync"/> needs for both
    /// <see cref="SubmitModuleReplyRequest.Locale"/> and <see cref="SubmitModuleReplyRequest.AcceptUnverifiedPhone"/>
    /// together, rather than two separate lookups of the identical row - see <see cref="ResolveLocaleAsync"/>'s
    /// own remarks for the missing-site default this shares.</summary>
    private async Task<(string Locale, bool AcceptUnverifiedPhone)> ResolveModuleContextAsync(
        SiteId siteId, CancellationToken cancellationToken)
    {
        var site = await sites.GetByIdAsync(siteId, cancellationToken);
        return ((site?.Locale ?? Locale.En).ToString(), site?.WidgetConfig.AcceptUnverifiedPhone ?? false);
    }

    /// <summary>`25-38`/`25-39`: the most recent phone number this visitor gave earlier in the
    /// conversation (`25-28`'s own contact capture, or an operator's own note - either source, the
    /// most recent one wins), self-reported and never proven reachable - see
    /// <see cref="SubmitModuleReplyRequest.KnownPhone"/>'s own remarks for what a module does with it.
    /// Null when nothing was ever recorded, the ordinary case for a visitor who never gave one.</summary>
    private async Task<string?> ResolveKnownPhoneAsync(VisitorId visitorId, CancellationToken cancellationToken)
    {
        var details = await contactDetails.GetForVisitorAsync(visitorId, cancellationToken);
        return details
            .Where(d => d.Kind == VisitorContactDetailKind.Phone)
            .OrderByDescending(d => d.RecordedAt)
            .FirstOrDefault()
            ?.Value;
    }

    /// <summary>
    /// `25-34`/`adr/0065`: every call site that needs to mutate the conversation's own aggregate state
    /// (a module task's start/step/close, plus the system message that always accompanies it) and
    /// persist that atomically funnels through here. <paramref name="applyMutations"/> is the whole
    /// decision, already made against the module's own already-received answer - this method's only
    /// job is to commit it, safely, under the two things that can go wrong once a caller stops relying
    /// on <c>EfInboxChecker</c>'s own combined <c>SaveChangesAsync()</c> to flush the conversation and
    /// its dedup row in lockstep the way it always used to (this handler's `25-34` root-cause writeup):
    /// a redelivery of the identical trigger, and an ordinary optimistic-concurrency conflict from any
    /// other concurrent writer of this same <see cref="Conversation"/> row.
    ///
    /// <para><b>Dedup first, unconditionally, before <paramref name="applyMutations"/> ever runs.</b>
    /// <see cref="IInboxChecker.TryRecordAndSaveAsync"/> is called here, standalone, with nothing else
    /// staged on the context - its own <c>SaveChangesAsync()</c> can only ever collide with itself
    /// (the dedup row's own unique key), never with the conversation's <c>xmin</c>, because the
    /// conversation has not been touched yet. That is what makes two genuinely concurrent deliveries
    /// of the *identical* trigger message resolve deterministically: exactly one of them ever sees
    /// <see langword="true"/> and proceeds past this point at all - the other returns
    /// <see cref="RouteConversationToModuleOutcome.AlreadyProcessed"/> immediately, with no reload, no
    /// retry, and nothing it staged to undo. A design that instead saved the conversation first and
    /// recorded the dedup row after (closer to the original shape) was tried on paper and rejected: the
    /// loser of that race would only discover the duplicate *after* successfully reapplying and
    /// committing its own copy of the same message, by which point the duplicate is already permanent -
    /// checking first is what makes "one message added" true by construction rather than by luck of the
    /// exact timing, which is exactly what this item's own test needs to be non-flaky.</para>
    ///
    /// <para><b>The trade-off this reordering accepts, stated rather than hidden.</b> Before this item,
    /// the conversation's own update, its message, its outbox row and the dedup row all committed in
    /// one <c>SaveChangesAsync()</c> - all-or-nothing. Splitting the dedup record from the conversation
    /// save (below) into two separate commits opens a narrow window: a process death between the two
    /// would leave this trigger marked processed with the conversation never actually updated, and
    /// because the dedup row already says "done", a broker redelivery would never retry the real work -
    /// a silent stall for that one trigger, requiring the visitor to send a new message (a new,
    /// unaffected <c>TriggerMessageId</c>) to unstick it, or an operator to notice and intervene. This
    /// is a genuinely new failure class this fix introduces, not a hidden one: it is narrower than it
    /// sounds (a crash in one specific gap, not routine concurrency) and sits in the same accepted
    /// "at-least-once cost, paid before the dedup point" category this handler's own type-level remarks
    /// already document for the module gateway call itself - but it is a real trade-off, not a free
    /// improvement, and a future sweep for "recorded but the conversation shows no matching effect"
    /// would be the honest way to close it if it ever matters in practice.</para>
    ///
    /// <para><b>Retry once on a genuine <see cref="Conversation"/> conflict, then a clean result -
    /// <see cref="Application.UseCases.CloseConversation.CloseConversationHandler"/>'s own established shape.</b> Once the
    /// dedup check above has passed, any concurrency conflict below is by definition *not* another
    /// delivery of this same trigger (the check already excluded that) - it is an unrelated writer
    /// (an operator's own action, another module task's own routing) bumping this row's `xmin` the same
    /// ordinary way `6-08`'s original finding describes. Reloading and reapplying is safe for the exact
    /// same reason it is safe there: <paramref name="applyMutations"/> never re-derives anything from
    /// outside data (the module gateway call, the channel-identity lookup) that could have gone stale -
    /// it only replays already-decided domain mutations, which is why <see cref="FinishStepAsync"/>'s
    /// own remarks are careful to fold every prior mutation (task start/step) into this same delegate
    /// rather than leaving any of it applied only once, before a conflict could ever roll it back.</para>
    /// </summary>
    private async Task<Result<RouteConversationToModuleOutcome>> AddSystemMessageAndSaveAsync(
        Conversation conversation, RouteConversationToModule command, RouteConversationToModuleOutcome outcome,
        Action<Conversation> applyMutations, CancellationToken cancellationToken)
    {
        var isFirstDelivery = await inbox.TryRecordAndSaveAsync(command.TriggerMessageId, ConsumerName, cancellationToken);
        if (!isFirstDelivery)
        {
            return RouteConversationToModuleOutcome.AlreadyProcessed;
        }

        try
        {
            return await ApplyAndSaveAsync(conversation, outcome, applyMutations, cancellationToken);
        }
        catch (ConversationConcurrencyConflictException)
        {
            var fresh = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
            if (fresh is null)
            {
                return ConversationErrors.NotFound(command.ConversationId.Value);
            }

            try
            {
                return await ApplyAndSaveAsync(fresh, outcome, applyMutations, cancellationToken);
            }
            catch (ConversationConcurrencyConflictException)
            {
                // `6-08`'s own bound, carried in unchanged: a second conflict inside this already-narrow
                // retry window means a third writer landed here, not that this attempt did anything
                // wrong. ModuleTaskConsumer turns this Result.Failure into a thrown exception, which its
                // own retry policy redelivers - and because the dedup row above is already recorded,
                // that redelivery will find this trigger AlreadyProcessed rather than trying again. That
                // is the same crash-window trade-off this method's own remarks state above, reached via
                // contention this time instead of a process death.
                return ConversationErrors.ConcurrencyConflict(command.ConversationId.Value);
            }
        }
    }

    /// <summary>One save attempt: apply the already-decided mutation, stage its outbox row, and save
    /// through <see cref="IConversationRepository.SaveAsync"/> - the port that translates a lost
    /// `xmin` check into <see cref="ConversationConcurrencyConflictException"/> rather than leaking
    /// `Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException` (`25-34`'s own root cause) up to
    /// <see cref="AddSystemMessageAndSaveAsync"/>'s retry wrapper. <see cref="InvalidConversationStateException"/>
    /// is caught here too, not just left to the caller: <paramref name="applyMutations"/> can call
    /// <see cref="Conversation.StartModuleTask"/>/<see cref="Conversation.RecordModuleStep"/>/
    /// <see cref="Conversation.CloseModuleTask"/>, and on a retry against freshly reloaded state those
    /// re-validate their own invariants against whatever is actually on disk now - exactly
    /// <see cref="Application.UseCases.CloseConversation.CloseConversationHandler"/>'s own reasoning for why its retry never
    /// bypasses a real business conflict, it only re-asks the same question against fresh data.</summary>
    private async Task<Result<RouteConversationToModuleOutcome>> ApplyAndSaveAsync(
        Conversation conversation, RouteConversationToModuleOutcome outcome, Action<Conversation> applyMutations,
        CancellationToken cancellationToken)
    {
        try
        {
            applyMutations(conversation);
        }
        catch (InvalidConversationStateException ex)
        {
            return ConversationErrors.InvalidState(ex.Message);
        }

        var domainEvent = conversation.DomainEvents.OfType<MessageAdded>().Last();
        outbox.Enqueue(MessageAcceptedMapper.ToEnvelope(domainEvent, idGenerator));
        // Cleared, so a later save of this same tracked aggregate cannot re-enqueue it - the same
        // "clear immediately after staging" discipline SendOfflineAutoReplyHandler's own remarks
        // describe. Found by a real test (HandleAsync_ASuccessfulOutcome_...LeavesNoDomainEventsBehind)
        // failing during this item's own build - the line was missing entirely, not merely misplaced.
        conversation.ClearDomainEvents();

        await conversations.SaveAsync(conversation, cancellationToken);
        return outcome;
    }
}
