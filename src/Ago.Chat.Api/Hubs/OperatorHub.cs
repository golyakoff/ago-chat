using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Cors;
using Ago.Chat.Api.Realtime;
using Ago.Chat.Application.Realtime;
using Ago.Chat.Application.UseCases.AssignConversation;
using Ago.Chat.Application.UseCases.GetConversationHistory;
using Ago.Chat.Application.UseCases.SendMessage;
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
[Authorize(AuthenticationSchemes = JwtSchemes.Operator)]
public sealed class OperatorHub(
    AssignConversationHandler assignConversation,
    SendOperatorMessageHandler sendMessage,
    GetConversationHistoryHandler getHistory,
    HubConnectionRegistration connectionRegistration,
    HubOriginValidator originValidator,
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

        var siteId = Context.User!.GetSiteId();
        if (!await originValidator.IsAllowedAsync(Context, siteId))
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

            return new HistoryPage(ToDtos(delta.Value), NextBeforeSequence: null);
        }

        var history = await getHistory.HandleAsOperatorAsync(
            new GetConversationHistoryAsOperator(id, operatorId, siteId, BeforeSequence: null, PageSize: 50),
            Context.ConnectionAborted);
        if (history.IsFailure)
        {
            throw new HubException(history.Error!.Value.Message);
        }

        return new HistoryPage(ToDtos(history.Value.Messages), history.Value.NextBeforeSequence);
    }

    public async Task<int> SendMessageAsync(Guid conversationId, string body, Guid? attachmentId = null)
    {
        var operatorId = Context.User!.GetOperatorId();
        var siteId = Context.User!.GetSiteId();
        var id = new ConversationId(conversationId);

        var sent = await sendMessage.HandleAsync(
            new SendOperatorMessage(id, operatorId, siteId, body, attachmentId is { } a ? new AttachmentId(a) : null),
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
        var dto = ToDto(sentMessage);
        await Clients.Caller.SendAsync("MessageReceived", dto, Context.ConnectionAborted);

        return sent.Value;
    }

    public async Task<HistoryPage> GetHistoryAsync(Guid conversationId, int? beforeSequence, int pageSize)
    {
        var operatorId = Context.User!.GetOperatorId();
        var siteId = Context.User!.GetSiteId();
        var page = await getHistory.HandleAsOperatorAsync(
            new GetConversationHistoryAsOperator(new ConversationId(conversationId), operatorId, siteId, beforeSequence, pageSize),
            Context.ConnectionAborted);
        if (page.IsFailure)
        {
            throw new HubException(page.Error!.Value.Message);
        }

        return new HistoryPage(ToDtos(page.Value.Messages), page.Value.NextBeforeSequence);
    }

    private static MessageDto ToDto(Application.Abstractions.MessageHistoryItem item) =>
        new(item.Id.Value, item.Sequence, item.AuthorKind.ToString(), item.AuthorId, item.Body, item.CreatedAt, item.AttachmentId?.Value);

    private static IReadOnlyList<MessageDto> ToDtos(IReadOnlyList<Application.Abstractions.MessageHistoryItem> items) =>
        [.. items.Select(ToDto)];
}
