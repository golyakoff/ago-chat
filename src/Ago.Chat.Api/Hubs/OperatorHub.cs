using Ago.Chat.Api.Auth;
using Ago.Chat.Application.UseCases.AssignConversation;
using Ago.Chat.Application.UseCases.GetConversationHistory;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
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
    GetConversationHistoryHandler getHistory) : Hub
{
    public async Task<HistoryPage> JoinConversationAsync(Guid conversationId)
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

        await Groups.AddToGroupAsync(Context.ConnectionId, VisitorHub.GroupName(id), Context.ConnectionAborted);

        var history = await getHistory.HandleAsOperatorAsync(
            new GetConversationHistoryAsOperator(id, operatorId, siteId, BeforeSequence: null, PageSize: 50),
            Context.ConnectionAborted);
        if (history.IsFailure)
        {
            throw new HubException(history.Error!.Value.Message);
        }

        return new HistoryPage(ToDtos(history.Value.Messages), history.Value.NextBeforeSequence);
    }

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

        var page = await getHistory.HandleAsOperatorAsync(
            new GetConversationHistoryAsOperator(id, operatorId, siteId, sent.Value + 1, 1), Context.ConnectionAborted);
        var sentMessage = page.Value.Messages.Single();
        await Clients.Group(VisitorHub.GroupName(id)).SendAsync(
            "MessageReceived", ToDto(sentMessage), Context.ConnectionAborted);

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
