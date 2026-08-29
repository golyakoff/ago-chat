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
/// <para><b>Idempotency (`CLAUDE.md` rule 5), and the one honestly-stated gap in it.</b> Every mutation
/// this handler makes to the tracked <see cref="Conversation"/> - starting a task, recording a step,
/// closing it, adding the system message, enqueuing the outbox row - is staged and then committed in
/// the single <see cref="IInboxChecker.TryRecordAndSaveAsync"/> call, the same
/// "stage everything, one save, one dedup row" shape `SendOfflineAutoReplyHandler`'s own remarks
/// describe. What that call <em>cannot</em> make idempotent is the call to <see cref="IModuleGateway"/>
/// itself, which happens <em>before</em> anything is staged (an HTTP call cannot sit inside a database
/// transaction - `CLAUDE.md`'s own boundary rules). A redelivered <c>MessageAccepted</c> - rare, but
/// possible under `adr/0017`'s at-least-once delivery - can therefore cause a second, wasted call to the
/// module (a second external task started, or a reply resubmitted) whose result is simply discarded when
/// the dedup save reports "already recorded". This is an accepted, at-least-once cost identical in kind
/// to every other side effect this codebase performs before its own dedup point (`resilience.md`'s own
/// idempotency-key discipline is what keeps it *safe* on the module's side, not what makes it
/// *free*) - stated here rather than left implicit, per the backlog item's own instruction.</para>
/// </summary>
public sealed class RouteConversationToModuleHandler(
    IConversationRepository conversations,
    IEnabledModuleReadStore moduleReadStore,
    IModuleGateway gateway,
    IOutboxWriter outbox,
    IInboxChecker inbox,
    IClock clock,
    IIdGenerator idGenerator)
{
    public const string ConsumerName = "module-task-routing";

    private const string ModuleUnavailableText =
        "Sorry, that's not available right now - a team member will help you shortly.";

    private const string ModuleBecameUnreachableText =
        "Sorry, something went wrong on our end - a person will take over from here.";

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
        var modulesForSite = await moduleReadStore.GetForSiteAsync(command.SiteId, cancellationToken);

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
        StartModuleTaskResult startResult;
        try
        {
            startResult = await gateway.StartTaskAsync(
                new EnabledModuleEndpoint(key, enabledModule.EntryPoint),
                new StartModuleTaskRequest(chatTaskId, command.SiteId, command.ConversationId, trigger.Body.Value),
                cancellationToken);
        }
        catch (ModuleUnreachableException)
        {
            // Nothing was ever started domain-side - there is no task to close, only an apology to add.
            return await AddSystemMessageAndSaveAsync(
                conversation, now, command, new MessageBody(ModuleUnavailableText), content: null,
                RouteConversationToModuleOutcome.ModuleUnavailableAtTrigger, cancellationToken);
        }

        conversation.StartModuleTask(
            new ModuleTaskId(chatTaskId), key, startResult.ExternalTaskId, now,
            startResult.Step.Kind, startResult.Step.Payload, startResult.Step.Actions);

        if (startResult.Complete)
        {
            conversation.CloseModuleTask(now);
        }

        var body = PrimitiveTextRenderer.Render(
            trigger.Body.Value, startResult.Step.Kind.Value, startResult.Step.Payload, startResult.Step.Actions);
        var content = MessageContent.Create(startResult.Step.Kind, startResult.Step.Payload, startResult.Step.Actions);

        return await AddSystemMessageAndSaveAsync(
            conversation, now, command, new MessageBody(body), content, RouteConversationToModuleOutcome.TaskStarted,
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
            // has nowhere to go, and the same escalation applies.
            conversation.CloseModuleTask(now);
            return await AddSystemMessageAndSaveAsync(
                conversation, now, command, new MessageBody(ModuleBecameUnreachableText), content: null,
                RouteConversationToModuleOutcome.Escalated, cancellationToken);
        }

        var value = ResolveReplyValue(trigger, active);
        if (value is null)
        {
            // Could not resolve - an out-of-range or non-numeric text-channel reply, or a widget reply
            // whose payload carried no usable value. The module is never called, and the task stays
            // open exactly as it was: no domain event, nothing to stage, nothing to save.
            return RouteConversationToModuleOutcome.ReplyNotResolved;
        }

        SubmitModuleReplyResult replyResult;
        try
        {
            replyResult = await gateway.SubmitReplyAsync(
                new EnabledModuleEndpoint(active.ModuleKey, enabledModule.EntryPoint),
                new SubmitModuleReplyRequest(active.ExternalTaskId, active.Id.Value, active.LastStepKind!.Value, value),
                cancellationToken);
        }
        catch (ModuleUnreachableException)
        {
            conversation.CloseModuleTask(now);
            return await AddSystemMessageAndSaveAsync(
                conversation, now, command, new MessageBody(ModuleBecameUnreachableText), content: null,
                RouteConversationToModuleOutcome.Escalated, cancellationToken);
        }

        if (replyResult.Step is { } step)
        {
            conversation.RecordModuleStep(step.Kind, step.Payload, step.Actions);
        }

        if (replyResult.Complete)
        {
            conversation.CloseModuleTask(now);
        }

        MessageBody body;
        MessageContent? content = null;
        if (replyResult.Step is { } s)
        {
            body = new MessageBody(PrimitiveTextRenderer.Render(trigger.Body.Value, s.Kind.Value, s.Payload, s.Actions));
            content = MessageContent.Create(s.Kind, s.Payload, s.Actions);
        }
        else
        {
            body = new MessageBody("Done - thank you.");
        }

        return await AddSystemMessageAndSaveAsync(
            conversation, now, command, body, content,
            replyResult.Complete ? RouteConversationToModuleOutcome.TaskCompleted : RouteConversationToModuleOutcome.StepAdvanced,
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

    private async Task<Result<RouteConversationToModuleOutcome>> AddSystemMessageAndSaveAsync(
        Conversation conversation, DateTimeOffset now, RouteConversationToModule command, MessageBody body,
        MessageContent? content, RouteConversationToModuleOutcome outcome, CancellationToken cancellationToken)
    {
        var messageId = new MessageId(idGenerator.NewId(now));
        conversation.AddSystemMessage(messageId, body, now, content: content);

        var domainEvent = conversation.DomainEvents.OfType<MessageAdded>().Last();
        outbox.Enqueue(MessageAcceptedMapper.ToEnvelope(domainEvent, idGenerator));
        // Cleared, so a later save of this same tracked aggregate cannot re-enqueue it - the same
        // "clear immediately after staging" discipline SendOfflineAutoReplyHandler's own remarks
        // describe. Found by a real test (HandleAsync_ASuccessfulOutcome_...LeavesNoDomainEventsBehind)
        // failing during this item's own build - the line was missing entirely, not merely misplaced.
        conversation.ClearDomainEvents();

        var isFirstDelivery = await inbox.TryRecordAndSaveAsync(command.TriggerMessageId, ConsumerName, cancellationToken);
        return isFirstDelivery ? outcome : RouteConversationToModuleOutcome.AlreadyProcessed;
    }
}
