using System.Text.Json;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
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
    IVisitorContactDetailRepository contactDetails)
{
    public const string ConsumerName = "module-task-routing";

    private const string ModuleUnavailableText =
        "Sorry, that's not available right now - a team member will help you shortly.";

    private const string ModuleBecameUnreachableText =
        "Sorry, something went wrong on our end - a person will take over from here.";

    /// <summary>`20-09`: what a visitor sees when a <see cref="PrimitiveKinds.VerifiedPhoneForm"/> reply
    /// names a phone number this system has not yet proven they can be reached on. Deliberately
    /// actionable rather than a generic refusal - see <see cref="ContinueActiveTaskAsync"/>'s own
    /// remarks for exactly what "verify it" means operationally today (no widget popup exists yet;
    /// `14-15`'s own HTTP endpoints are the real mechanism, driven directly until one does).</summary>
    private const string PhoneVerificationRequiredText =
        "Before I can book that, please verify this phone number - we'll send you a code by SMS or call.";

    /// <summary>`19-03`: the fallback <see cref="PrimitiveTextRenderer.Render"/> falls back to when a
    /// module's own escalate step carries no <c>payload.prompt</c> of its own. Deliberately not the
    /// visitor's own trigger/reply text (which is what every other kind's fallback is) - showing a
    /// visitor their own last message back at them as the "reason" for handing off reads as a bug, not
    /// as an apology.</summary>
    private const string ModuleEscalatedFallbackText =
        "Let me get a team member to help with that.";

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

        // `20-07`'s own id trick: Chat's own ModuleTaskId doubles as the wire contract's `chatTaskId` -
        // the module is handed exactly the id this aggregate will use to identify the task once
        // StartModuleTask below succeeds, so no second id has to be invented or reconciled.
        var chatTaskId = idGenerator.NewId(now);
        // `25-37`: the site's own configured widget language, resolved once here and handed to the
        // module for its very first step - see ResolveLocaleAsync's own remarks for why a missing site
        // reads as the safe default rather than a hard failure.
        var locale = await ResolveLocaleAsync(command.SiteId, cancellationToken);
        StartModuleTaskResult startResult;
        try
        {
            startResult = await gateway.StartTaskAsync(
                new EnabledModuleEndpoint(key, command.SiteId, enabledModule.EntryPoint, enabledModule.Credential),
                new StartModuleTaskRequest(chatTaskId, command.SiteId, command.ConversationId, trigger.Body.Value, locale),
                cancellationToken);
        }
        catch (ModuleUnreachableException)
        {
            // Nothing was ever started domain-side - there is no task to close, only an apology to add.
            var messageId = new MessageId(idGenerator.NewId(now));
            return await AddSystemMessageAndSaveAsync(
                conversation, command, RouteConversationToModuleOutcome.ModuleUnavailableAtTrigger,
                c => c.AddSystemMessage(messageId, new MessageBody(ModuleUnavailableText), now, content: null),
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
            now, command,
            c => c.StartModuleTask(
                new ModuleTaskId(chatTaskId), key, startResult.ExternalTaskId, now,
                startResult.Step.Kind, startResult.Step.Payload, startResult.Step.Actions),
            cancellationToken);
    }

    private async Task<Result<RouteConversationToModuleOutcome>> ContinueActiveTaskAsync(
        Conversation conversation, ModuleTask active, Message trigger, IReadOnlyList<EnabledModuleSummary> modulesForSite,
        DateTimeOffset now, RouteConversationToModule command, CancellationToken cancellationToken)
    {
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
                    c.AddSystemMessage(messageId, new MessageBody(ModuleBecameUnreachableText), now, content: null);
                },
                cancellationToken);
        }

        var value = ResolveReplyValue(trigger, active);
        if (value is null)
        {
            // Could not resolve - an out-of-range or non-numeric text-channel reply, or a widget reply
            // whose payload carried no usable value. The module is never called, and the task stays
            // open exactly as it was: no domain event, nothing to stage, nothing to save.
            return RouteConversationToModuleOutcome.ReplyNotResolved;
        }

        // `20-09`: the structural gate. A reply against a step this vocabulary marks as needing a
        // *verified* phone is checked against `14-15`'s own evidence - an active ChannelIdentity for
        // this exact (site, Sms, phone) triple, owned by this conversation's own visitor - before the
        // module is ever called, the identical "recognise the kind by value, act before forwarding"
        // shape `PrimitiveKinds.Escalate`'s own handling already established (`adr/0081`). Calendar
        // never sees this decision; it only ever sees its result (a timestamp, or no reply at all).
        //
        // A phone that does not even parse (not this vocabulary's concern - shape validation is
        // Calendar's own `PhoneNumber`, the same "Chat never re-validates what a module already
        // validates" split `ReplyToModuleTaskHandler`'s own remarks describe for a reply's id) is
        // forwarded unchanged, exactly as an ordinary `form` reply always has been: Calendar's own
        // `BookEventHandler` turns it into `booking.invalid_phone`, and the existing lost-race
        // re-offer path handles it from there - no second validation invented here.
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
                    // Not verified for this visitor - never forwarded. The task stays open (the visitor
                    // can retype the identical number once verification actually completes, through
                    // `14-15`'s own endpoints - there is no widget popup wired to trigger them yet,
                    // `20-09`'s own report names this as the deferred, frontend-side follow-up).
                    var messageId = new MessageId(idGenerator.NewId(now));
                    return await AddSystemMessageAndSaveAsync(
                        conversation, command, RouteConversationToModuleOutcome.PhoneVerificationRequired,
                        c => c.AddSystemMessage(messageId, new MessageBody(PhoneVerificationRequiredText), now, content: null),
                        cancellationToken);
                }

                // Verified - and has been since `identity.FirstSeenAt` (`ChannelIdentity.Link`'s own
                // instant, never touched again by `Touch`), which is the honest answer to "since when"
                // rather than "now": Calendar snapshots this value verbatim (`20-09`'s own "cross-product
                // data question" - Chat asserts, Calendar trusts, the identical `adr/0077` boundary
                // `20-07`'s module-task endpoints already accept).
                phoneVerifiedAt = identity.FirstSeenAt;
            }
        }

        // `25-37`/`25-38`/`25-39`: three more facts a module may need to answer this reply, resolved
        // fresh on every call rather than remembered anywhere - see SubmitModuleReplyRequest.Locale's
        // own remarks for why this item spends no migration on persisting them. Read unconditionally
        // (not gated to a particular step kind): none of the three reads is expensive - a single-row
        // Site lookup and a visitor's own small, bounded contact-detail list
        // (VisitorContactDetail's own remarks) - and gating on step kind would make this handler's own
        // behaviour depend on knowledge of which primitive kind a booking module happens to use its
        // phone step for, which is exactly the "no special-casing per primitive kind" constraint this
        // handler's own type remarks already hold ResolveReplyValue to.
        var (locale, acceptUnverifiedPhone) = await ResolveModuleContextAsync(conversation.SiteId, cancellationToken);
        var knownPhone = await ResolveKnownPhoneAsync(conversation.VisitorId, cancellationToken);

        SubmitModuleReplyResult replyResult;
        try
        {
            replyResult = await gateway.SubmitReplyAsync(
                new EnabledModuleEndpoint(active.ModuleKey, conversation.SiteId, enabledModule.EntryPoint, enabledModule.Credential),
                new SubmitModuleReplyRequest(
                    active.ExternalTaskId, active.Id.Value, active.LastStepKind!.Value, value, phoneVerifiedAt,
                    locale, knownPhone, acceptUnverifiedPhone),
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
                    c.AddSystemMessage(messageId, new MessageBody(ModuleBecameUnreachableText), now, content: null);
                },
                cancellationToken);
        }

        if (replyResult.Step is { } step)
        {
            var nonEscalationOutcome = replyResult.Complete
                ? RouteConversationToModuleOutcome.TaskCompleted
                : RouteConversationToModuleOutcome.StepAdvanced;
            return await FinishStepAsync(
                conversation, trigger, step, replyResult.Complete, nonEscalationOutcome, now, command,
                c => c.RecordModuleStep(step.Kind, step.Payload, step.Actions),
                cancellationToken);
        }

        // No further step: the module's own "done" with nothing to add - unaffected by `19-03`, since
        // an escalate step always carries a step (that is the whole signal); a module that wants to hand
        // off with literally nothing to say still has to say so through a step, not through silence.
        var doneMessageId = new MessageId(idGenerator.NewId(now));
        return await AddSystemMessageAndSaveAsync(
            conversation, command, RouteConversationToModuleOutcome.TaskCompleted,
            c =>
            {
                c.CloseModuleTask(now);
                c.AddSystemMessage(doneMessageId, new MessageBody("Done - thank you."), now, content: null);
            },
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
        Action<Conversation> applyStep, CancellationToken cancellationToken)
    {
        var isEscalation = step.Kind.Value == PrimitiveKinds.Escalate;
        var fallback = isEscalation ? ModuleEscalatedFallbackText : trigger.Body.Value;
        var body = PrimitiveTextRenderer.Render(fallback, step.Kind.Value, step.Payload, step.Actions);
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
