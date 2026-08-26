using System.Diagnostics;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Cors;
using Ago.Chat.Api.Realtime;
using Ago.Chat.Application.Realtime;
using Ago.Chat.Application.UseCases.AssignConversation;
using Ago.Chat.Application.UseCases.GetConversationHistory;
using Ago.Chat.Application.UseCases.GetVisitorPresence;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Module;
using Ago.Platform.Abstractions;
using Ago.Platform.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Ago.Chat.Api.Hubs;

/// <summary>realtime.md: authenticated by the operator's JWT, scoped to one site. Who issues that
/// token is `1-06`'s dev-only stub today, OIDC at Stage 5 (authorization.md) - this hub does not
/// change either way, since both are JWTs.</summary>
// `5-05`: the policy, not just the scheme - a Keycloak token can validate (real signature, real
// issuer) yet resolve to no known operator (OperatorIdentityClaimsTransformation adds no OperatorId
// claim), and RequireOperatorIdentity is what rejects that cleanly rather than
// ClaimsPrincipalExtensions.GetOperatorId throwing deep inside a hub method.
[Authorize(AuthenticationSchemes = JwtSchemes.Operator, Policy = "RequireOperatorIdentity")]
public sealed class OperatorHub(
    AssignConversationHandler assignConversation,
    SendOperatorMessageHandler sendMessage,
    GetConversationHistoryHandler getHistory,
    GetVisitorPresenceHandler getVisitorPresence,
    HubConnectionRegistration connectionRegistration,
    ConsoleOriginValidator consoleOrigin,
    OperatorPresencePublisher presencePublisher,
    DrainState drainState) : Hub
{
    /// <summary>Same wiring as VisitorHub.OnConnectedAsync - see its comment, including the `3-06`
    /// drain check and `5-01`'s origin check.</summary>
    public override async Task OnConnectedAsync()
    {
        if (drainState.IsDraining)
        {
            Context.Abort();
            return;
        }

        // `5-18`: the **console's** origin, not this tenant's widget origins.
        //
        // This used to call HubOriginValidator with the operator's own SiteId, which asks
        // "may a page at this origin embed this tenant's widget" - a question about visitors that an
        // operator's connection has no reason to satisfy. The live consequence was total: a tenant
        // whose AllowedOrigins did not happen to contain the console (every tenant `8-07` mints, whose
        // list is exactly the demo shop page) had every operator connection aborted here, immediately
        // after a successful SignalR handshake. That produces a clean close, so nothing logged, nothing
        // fell back to another transport, and nothing was ever registered in Redis - the connection
        // simply ended and the console said "Offline".
        if (!consoleOrigin.IsAllowed(Context))
        {
            Context.Abort();
            return;
        }

        var principal = PrincipalKeys.ForOperator(Context.User!.GetOperatorId());
        await connectionRegistration.OnConnectedAsync(new ConnectionId(Context.ConnectionId), principal, Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }

    /// <summary>`4-04`: the query-at-disconnect fast path - if this was the operator's last
    /// connection anywhere, publish immediately rather than waiting for the periodic sweep
    /// (`OperatorDisconnectSweepJob`, `Ago.Chat.Worker`) to notice. The sweep is still the
    /// backstop this relies on for a disconnect that never fires this event at all (a hard
    /// process kill on the client side, or this very host dying before the publish completes).</summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var operatorId = Context.User!.GetOperatorId();
        var principal = PrincipalKeys.ForOperator(operatorId);
        var lastConnectionGone = await connectionRegistration.OnDisconnectedAsync(new ConnectionId(Context.ConnectionId), principal);
        if (lastConnectionGone)
        {
            await presencePublisher.PublishLostAsync(operatorId, Context.User!.GetSiteId(), CancellationToken.None);
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// `3-03`: <paramref name="lastKnownSequence"/> is the reconnect case - see
    /// <c>VisitorHub.JoinAsync</c>'s remarks, the same reasoning applies here. Calling
    /// <c>AssignConversationHandler</c> again on every join, including a reconnect, is what makes
    /// <c>Conversation.AssignTo</c>'s same-operator no-op (`3-03`) load-bearing: without it, an
    /// operator reconnecting to a conversation they already hold would fail this call outright.
    /// </summary>
    public async Task<HistoryPage> JoinConversationAsync(Guid conversationId, int? lastKnownSequence = null)
    {
        var operatorId = Context.User!.GetOperatorId();
        var siteId = Context.User!.GetSiteId();
        var id = new ConversationId(conversationId);

        var assigned = await assignConversation.HandleAsync(
            new AssignConversation(id, operatorId, siteId), Context.ConnectionAborted);
        if (assigned.IsFailure)
        {
            throw new HubException(assigned.Error!.Value.Message);
        }

        if (lastKnownSequence is { } afterSequence)
        {
            var delta = await getHistory.HandleDeltaAsOperatorAsync(
                new GetConversationDeltaAsOperator(id, operatorId, siteId, afterSequence), Context.ConnectionAborted);
            if (delta.IsFailure)
            {
                throw new HubException(delta.Error!.Value.Message);
            }

            return new HistoryPage(ToDtos(delta.Value, id), NextBeforeSequence: null);
        }

        var history = await getHistory.HandleAsOperatorAsync(
            new GetConversationHistoryAsOperator(id, operatorId, siteId, BeforeSequence: null, PageSize: 50),
            Context.ConnectionAborted);
        if (history.IsFailure)
        {
            throw new HubException(history.Error!.Value.Message);
        }

        return new HistoryPage(ToDtos(history.Value.Messages, id), history.Value.NextBeforeSequence);
    }

    // `5-07`: clientMessageId appended last, after attachmentId - SignalR's client binder matches
    // invocations by argument count against this exact parameter order (local-dev.md's own
    // documented gotcha), so a caller built before this shipped (dev-harness.html, ago-widget's
    // VisitorConnection, which calls the visitor-side twin of this method with 2 or 3 positional
    // args) keeps binding correctly with clientMessageId simply omitted - inserting it earlier would
    // have silently reinterpreted an existing 3-argument call's attachmentId as a clientMessageId.
    // `7-01`: same trace-root shape as VisitorHub.SendMessageAsync - see its own remarks.
    // `7-02`: same placement decision as VisitorHub.SendMessageAsync - see its own remarks.
    // `14-06`: three optional parameters appended last, after clientMessageId. Position matters for a
    // hub method in a way it does not for a DTO (this file's own `5-07` note): a client built before
    // this shipped calls SendMessageAsync with four arguments or fewer and keeps working, because
    // SignalR binds hub arguments positionally and fills the rest with their defaults. `content` is a
    // string here rather than a JsonElement - it is what the caller typed, unvalidated, and turning
    // it into a validated value object is the handler's job (StructuredContentBinder), so that a
    // malformed one is a rejection and not a 500.
    public async Task<int> SendMessageAsync(
        Guid conversationId, string body, Guid? attachmentId = null, Guid? clientMessageId = null,
        string? contentKind = null, string? content = null, IReadOnlyList<MessageActionInput>? actions = null)
    {
        using var activity = ChatTracing.Source.StartActivity(ChatTracing.SpanNames.HubSendMessage, ActivityKind.Server);
        var stopwatch = Stopwatch.StartNew();
        var success = false;

        try
        {
            var operatorId = Context.User!.GetOperatorId();
            var siteId = Context.User!.GetSiteId();
            var id = new ConversationId(conversationId);

            var sent = await sendMessage.HandleAsync(
                new SendOperatorMessage(
                    id, operatorId, siteId, body, attachmentId is { } a ? new AttachmentId(a) : null, clientMessageId,
                    activity?.Id, contentKind, content, actions),
                Context.ConnectionAborted);
            if (sent.IsFailure)
            {
                throw new HubException(sent.Error!.Value.Message);
            }

            // 3-02: local echo only, same reasoning as VisitorHub.EchoToCallerAsync - the real delivery
            // to every participant (including this operator's other tabs and the visitor) goes through
            // ConnectionFanoutConsumer reacting to this message's own MessageAccepted.
            var page = await getHistory.HandleAsOperatorAsync(
                new GetConversationHistoryAsOperator(id, operatorId, siteId, sent.Value + 1, 1), Context.ConnectionAborted);
            var sentMessage = page.Value.Messages.Single();
            var dto = ToDto(sentMessage, id);
            await Clients.Caller.SendAsync("MessageReceived", dto, Context.ConnectionAborted);

            success = true;
            return sent.Value;
        }
        finally
        {
            ChatMetrics.RecordHubMethod("OperatorHub", "SendMessage", stopwatch.Elapsed, success);
        }
    }

    public async Task<HistoryPage> GetHistoryAsync(Guid conversationId, int? beforeSequence, int pageSize)
    {
        var operatorId = Context.User!.GetOperatorId();
        var siteId = Context.User!.GetSiteId();
        var id = new ConversationId(conversationId);
        var page = await getHistory.HandleAsOperatorAsync(
            new GetConversationHistoryAsOperator(id, operatorId, siteId, beforeSequence, pageSize),
            Context.ConnectionAborted);
        if (page.IsFailure)
        {
            throw new HubException(page.Error!.Value.Message);
        }

        return new HistoryPage(ToDtos(page.Value.Messages, id), page.Value.NextBeforeSequence);
    }

    /// <summary>
    /// `5-07`: a snapshot, not a subscription - see `GetVisitorPresenceHandler`'s own remarks for why
    /// a periodic re-call from the console is the right shape here rather than a new push mechanism.
    /// </summary>
    public async Task<bool> GetVisitorPresenceAsync(Guid conversationId)
    {
        var operatorId = Context.User!.GetOperatorId();
        var siteId = Context.User!.GetSiteId();

        var presence = await getVisitorPresence.HandleAsync(
            new GetVisitorPresence(new ConversationId(conversationId), operatorId, siteId), Context.ConnectionAborted);
        if (presence.IsFailure)
        {
            throw new HubException(presence.Error!.Value.Message);
        }

        return presence.Value;
    }

    // `14-06`: the mapping moved to Ago.Chat.Application.Mapping.MessageDtoMapper - it existed
    // identically here, in OperatorHub and in ResolveMessageDeliveryTargetsHandler, and a message a
    // client sees must not depend on which of the three delivered it (`5-11`'s own failure mode).
    private static MessageDto ToDto(Application.Abstractions.MessageHistoryItem item, ConversationId conversationId) =>
        MessageDtoMapper.ToDto(item, conversationId);

    private static IReadOnlyList<MessageDto> ToDtos(IReadOnlyList<Application.Abstractions.MessageHistoryItem> items, ConversationId conversationId) =>
        MessageDtoMapper.ToDtos(items, conversationId);
}
