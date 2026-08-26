using System.Diagnostics;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Cors;
using Ago.Chat.Api.Realtime;
using Ago.Chat.Application.Realtime;
using Ago.Chat.Application.UseCases.GetConversationHistory;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Application.UseCases.StartConversation;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Ago.Chat.Api.Hubs;

/// <summary>
/// realtime.md: authenticated by a signed visitor token, scoped to one site. A hub method maps
/// argument -&gt; command, dispatches, maps the result back - no business logic here
/// (clean-architecture.md).
/// </summary>
[Authorize(AuthenticationSchemes = JwtSchemes.Visitor)]
public sealed class VisitorHub(
    StartConversationHandler startConversation,
    SendVisitorMessageHandler sendMessage,
    GetConversationHistoryHandler getHistory,
    HubConnectionRegistration connectionRegistration,
    HubOriginValidator originValidator,
    DrainState drainState) : Hub
{
    private const int DefaultPageSize = 50;

    /// <summary>3-01: registers this connection so any node can later resolve "where is this
    /// visitor" (realtime.md). Site/visitor identity comes from the JWT, already validated before a
    /// hub method or lifecycle event ever runs.
    ///
    /// 3-06: "stop accepting new hub connections" once this node starts draining - readiness
    /// already routes new traffic elsewhere, but a connection already in flight when that
    /// propagates must not be allowed to settle in only to be told to reconnect a moment later.
    ///
    /// 5-01: a WebSocket upgrade's `Origin` is not covered by the CORS policy the way plain HTTP is
    /// (`HubOriginValidator`'s own remarks) - this is the actual enforcement point for a hub
    /// connection, checked before registering anything so a rejected connection never gets recorded.
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        if (drainState.IsDraining)
        {
            Context.Abort();
            return;
        }

        var siteId = Context.User!.GetSiteId();
        if (!await originValidator.IsAllowedAsync(Context, siteId))
        {
            Context.Abort();
            return;
        }

        var principal = PrincipalKeys.ForVisitor(Context.User!.GetVisitorId());
        await connectionRegistration.OnConnectedAsync(new ConnectionId(Context.ConnectionId), principal, Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var principal = PrincipalKeys.ForVisitor(Context.User!.GetVisitorId());
        await connectionRegistration.OnDisconnectedAsync(new ConnectionId(Context.ConnectionId), principal);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Called once, right after connecting - starts or resumes the visitor's conversation.
    ///
    /// `3-03`: <paramref name="lastKnownSequence"/> is how a client that dropped and reconnected asks
    /// for exactly what it missed (realtime.md's Client protocol section) instead of the usual most-
    /// recent page - the client already has everything up to that sequence, so replaying it again
    /// would duplicate what is already on screen. Ignored for a conversation this call itself just
    /// created (<c>IsNew</c>): a sequence remembered from a previous conversation means nothing here,
    /// there is nothing to have missed.
    /// </summary>
    public async Task<VisitorJoinResult> JoinAsync(int? lastKnownSequence = null)
    {
        var siteId = Context.User!.GetSiteId();
        var visitorId = Context.User!.GetVisitorId();

        var started = await startConversation.HandleAsync(
            new StartConversation(siteId, visitorId), Context.ConnectionAborted);
        var conversationId = started.Value.ConversationId;

        if (lastKnownSequence is { } afterSequence && !started.Value.IsNew)
        {
            var delta = await getHistory.HandleDeltaAsVisitorAsync(
                new GetConversationDeltaAsVisitor(conversationId, visitorId, afterSequence), Context.ConnectionAborted);
            return new VisitorJoinResult(conversationId.Value, IsNew: false, ToDtos(delta.Value, conversationId));
        }

        var history = await getHistory.HandleAsVisitorAsync(
            new GetConversationHistoryAsVisitor(conversationId, visitorId, BeforeSequence: null, DefaultPageSize),
            Context.ConnectionAborted);

        return new VisitorJoinResult(conversationId.Value, started.Value.IsNew, ToDtos(history.Value.Messages, conversationId));
    }

    // `5-07`: clientMessageId appended last, after attachmentId - see OperatorHub.SendMessageAsync's
    // matching comment for why the position (not just optionality) matters for every caller built
    // before this shipped.
    //
    // `7-01`: the trace root for nfr.md's "hub -> handler -> DB -> outbox -> broker -> consumer ->
    // delivery" - a Server-kind span (SignalR is this hub method's own transport, the same role
    // ASP.NET Core instrumentation gives an ordinary HTTP request) that instrumentation packages
    // cannot see on their own, since a SignalR hub invocation is not itself a fresh HTTP request.
    // Its own traceparent is threaded through the command/PendingMessage as plain data (that record's
    // own remarks) rather than Application ever touching System.Diagnostics.Activity directly.
    // `7-02`: nfr.md's "RED metrics per... hub method" - recorded at the same span boundary 7-01
    // already named (ChatTracing.SpanNames.HubSendMessage above), not for every hub method: this is
    // the one method that already has its own manual span boundary to attach a duration/outcome
    // measurement to; JoinAsync/GetHistoryAsync are plain request/response calls ASP.NET Core-style
    // RED does not reach either (no HTTP request of their own), but adding a boundary to them purely
    // for this item was judged out of this item's own literal scope - see the handback report.
    // `14-06`, corrected by `5-19`: structured content does NOT ride on this method. It was appended
    // here as three optional parameters, on the claim that "SignalR binds hub arguments positionally
    // and fills the rest with their defaults" - it does not, and every deployed client's send failed
    // at once. See SendStructuredMessageAsync below, and HubMethodArityTests for the rule and its
    // proof.
    public Task<int> SendMessageAsync(
        Guid conversationId, string body, Guid? attachmentId = null, Guid? clientMessageId = null) =>
        SendAsync(conversationId, body, attachmentId, clientMessageId, null, null, null);

    /// <summary>
    /// `5-19`: `14-06`'s structured content, on a method of its own.
    ///
    /// <para><b>A second method, not three more parameters.</b> A hub method's parameter list is part
    /// of a contract with clients that are already embedded on other people's sites and cannot be
    /// forced to upgrade - SignalR refuses an invocation that supplies fewer arguments than the target
    /// declares, so growing the list breaks every one of them at once, silently, with no server-side
    /// log at all. That is the same reasoning `adr/0048` used for token renewal ("a second endpoint,
    /// not a flag on the mint") and the same one `8-11` used to keep `data-public-demo` working as an
    /// alias. `api-design.md` states it about routes; a hub method is the same promise in a different
    /// shape.</para>
    ///
    /// <para>Nothing calls this yet - `14-06` shipped the envelope and `20-06`/`21-01` fill it. A new
    /// method with no caller is the honest cost of not breaking the old one; the alternative was a
    /// working new capability and a dead product.</para>
    ///
    /// <para><c>content</c> is a string rather than a <c>JsonElement</c> - it is what the caller
    /// typed, unvalidated, and turning it into a validated value object is the handler's job
    /// (<c>StructuredContentBinder</c>), so that a malformed one is a rejection and not a 500.</para>
    /// </summary>
    public Task<int> SendStructuredMessageAsync(
        Guid conversationId, string body, Guid? attachmentId, Guid? clientMessageId,
        string? contentKind, string? content, IReadOnlyList<MessageActionInput>? actions) =>
        SendAsync(conversationId, body, attachmentId, clientMessageId, contentKind, content, actions);

    /// <summary>
    /// The one implementation both hub methods above delegate to. Private, so it is not itself a hub
    /// method and its parameter list is nobody's contract - which is the point: the arity rule binds
    /// what SignalR can invoke, and this is free to grow when `21-01` needs it to.
    /// </summary>
    private async Task<int> SendAsync(
        Guid conversationId, string body, Guid? attachmentId, Guid? clientMessageId,
        string? contentKind, string? content, IReadOnlyList<MessageActionInput>? actions)
    {
        using var activity = ChatTracing.Source.StartActivity(ChatTracing.SpanNames.HubSendMessage, ActivityKind.Server);
        var stopwatch = Stopwatch.StartNew();
        var success = false;

        try
        {
            var visitorId = Context.User!.GetVisitorId();
            var id = new ConversationId(conversationId);

            var sent = await sendMessage.HandleAsync(
                new SendVisitorMessage(
                    id, visitorId, body, attachmentId is { } a ? new AttachmentId(a) : null, clientMessageId,
                    activity?.Id, contentKind, content, actions),
                Context.ConnectionAborted);
            if (sent.IsFailure)
            {
                throw new HubException(sent.Error!.Value.Message);
            }

            await EchoToCallerAsync(id, visitorId, sent.Value);
            success = true;
            return sent.Value;
        }
        finally
        {
            // Both hub methods report under one name: they are one operation reached two ways, and
            // splitting the RED series would make a dashboard that already exists lose half its
            // traffic the day a client starts using the new method.
            ChatMetrics.RecordHubMethod("VisitorHub", "SendMessage", stopwatch.Elapsed, success);
        }
    }

    public async Task<HistoryPage> GetHistoryAsync(Guid conversationId, int? beforeSequence, int pageSize)
    {
        var visitorId = Context.User!.GetVisitorId();
        var id = new ConversationId(conversationId);
        var page = await getHistory.HandleAsVisitorAsync(
            new GetConversationHistoryAsVisitor(id, visitorId, beforeSequence, pageSize),
            Context.ConnectionAborted);
        if (page.IsFailure)
        {
            throw new HubException(page.Error!.Value.Message);
        }

        return new HistoryPage(ToDtos(page.Value.Messages, id), page.Value.NextBeforeSequence);
    }

    /// <summary>
    /// 3-02: an immediate local echo to the sender's own connection, without waiting on the broker
    /// round trip - a pure latency optimisation (a same-node fast path is legitimate; a second,
    /// divergent delivery mechanism is not). Every other participant - other tabs of this same
    /// visitor, the assigned operator, and technically this same connection again - is reached
    /// through the one real delivery path: <c>ConnectionFanoutConsumer</c> (`Ago.Chat.Worker`)
    /// reacts to this message's own `MessageAccepted` and publishes through
    /// <c>INodeFanoutPublisher</c> (realtime.md's Fan-out path). The resulting duplicate push back to
    /// this same connection is accepted, not special-cased away: messaging.md's client-side dedupe by
    /// message id already covers a redelivered push the same way it covers a redelivered broker
    /// message.
    /// </summary>
    private async Task EchoToCallerAsync(ConversationId conversationId, VisitorId visitorId, int sequence)
    {
        var page = await getHistory.HandleAsVisitorAsync(
            new GetConversationHistoryAsVisitor(conversationId, visitorId, sequence + 1, 1), Context.ConnectionAborted);
        var sentMessage = page.Value.Messages.Single();
        var dto = ToDto(sentMessage, conversationId);
        await Clients.Caller.SendAsync("MessageReceived", dto, Context.ConnectionAborted);
    }

    // `14-06`: the mapping moved to Ago.Chat.Application.Mapping.MessageDtoMapper - it existed
    // identically here, in OperatorHub and in ResolveMessageDeliveryTargetsHandler, and a message a
    // client sees must not depend on which of the three delivered it (`5-11`'s own failure mode).
    private static MessageDto ToDto(Application.Abstractions.MessageHistoryItem item, ConversationId conversationId) =>
        MessageDtoMapper.ToDto(item, conversationId);

    private static IReadOnlyList<MessageDto> ToDtos(IReadOnlyList<Application.Abstractions.MessageHistoryItem> items, ConversationId conversationId) =>
        MessageDtoMapper.ToDtos(items, conversationId);
}
