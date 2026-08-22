using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Realtime;
using Ago.Chat.Application.Realtime;
using Ago.Chat.Application.UseCases.AssignConversation;
using Ago.Chat.Application.UseCases.GetConversationHistory;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
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
    HubConnectionRegistration connectionRegistration) : Hub
{
    /// <summary>Same wiring as VisitorHub.OnConnectedAsync - see its comment.</summary>
    public override async Task OnConnectedAsync()
    {
        var principal = PrincipalKeys.ForOperator(Context.User!.GetOperatorId());
        await connectionRegistration.OnConnectedAsync(new ConnectionId(Context.ConnectionId), principal, Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var principal = PrincipalKeys.ForOperator(Context.User!.GetOperatorId());
        await connectionRegistration.OnDisconnectedAsync(new ConnectionId(Context.ConnectionId), principal);
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

    /// <summary>`3-03` stub - see <c>VisitorHub.ReconnectAsync</c>'s remarks.</summary>
    public Task ReconnectAsync(TimeSpan after) =>
        Clients.Caller.SendAsync("Reconnect", new { after }, Context.ConnectionAborted);

    public async Task<int> SendMessageAsync(Guid conversationId, string body)
    {
        var operatorId = Context.User!.GetOperatorId();
        var siteId = Context.User!.GetSiteId();
        var id = new ConversationId(conversationId);

        var sent = await sendMessage.HandleAsync(new SendOperatorMessage(id, operatorId, siteId, body), Context.ConnectionAborted);
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
        new(item.Id.Value, item.Sequence, item.AuthorKind.ToString(), item.AuthorId, item.Body, item.CreatedAt);

    private static IReadOnlyList<MessageDto> ToDtos(IReadOnlyList<Application.Abstractions.MessageHistoryItem> items) =>
        [.. items.Select(ToDto)];
}
