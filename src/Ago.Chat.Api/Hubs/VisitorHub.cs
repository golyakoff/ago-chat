using Ago.Chat.Api.Auth;
using Ago.Chat.Application.UseCases.GetConversationHistory;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Application.UseCases.StartConversation;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
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
    IHubContext<OperatorHub> operatorHub) : Hub
{
    private const int DefaultPageSize = 50;

    /// <summary>Called once, right after connecting - starts or resumes the visitor's conversation
    /// and joins its group, so a later reply reaches this connection.</summary>
    public async Task<VisitorJoinResult> JoinAsync()
    {
        var siteId = Context.User!.GetSiteId();
        var visitorId = Context.User!.GetVisitorId();

        var started = await startConversation.HandleAsync(
            new StartConversation(siteId, visitorId), Context.ConnectionAborted);
        var conversationId = started.Value.ConversationId;

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(conversationId), Context.ConnectionAborted);

        var history = await getHistory.HandleAsVisitorAsync(
            new GetConversationHistoryAsVisitor(conversationId, visitorId, BeforeSequence: null, DefaultPageSize),
            Context.ConnectionAborted);

        return new VisitorJoinResult(conversationId.Value, started.Value.IsNew, ToDtos(history.Value.Messages));
    }

    public async Task<int> SendMessageAsync(Guid conversationId, string body)
    {
        var visitorId = Context.User!.GetVisitorId();
        var id = new ConversationId(conversationId);

        var sent = await sendMessage.HandleAsync(new SendVisitorMessage(id, visitorId, body), Context.ConnectionAborted);
        if (sent.IsFailure)
        {
            throw new HubException(sent.Error!.Value.Message);
        }

        await BroadcastAsync(id, visitorId, sent.Value);
        return sent.Value;
    }

    public async Task<HistoryPage> GetHistoryAsync(Guid conversationId, int? beforeSequence, int pageSize)
    {
        var visitorId = Context.User!.GetVisitorId();
        var page = await getHistory.HandleAsVisitorAsync(
            new GetConversationHistoryAsVisitor(new ConversationId(conversationId), visitorId, beforeSequence, pageSize),
            Context.ConnectionAborted);
        if (page.IsFailure)
        {
            throw new HubException(page.Error!.Value.Message);
        }

        return new HistoryPage(ToDtos(page.Value.Messages), page.Value.NextBeforeSequence);
    }

    /// <summary>
    /// SignalR hubs are isolated from each other - a "conversation:X" group in <see cref="VisitorHub"/>
    /// and one of the same name in <see cref="OperatorHub"/> are two different groups, each scoped to
    /// its own <c>HubLifetimeManager&lt;THub&gt;</c> (found by running this: the operator's own
    /// connection always received its own broadcast; the visitor's connection never did, from any
    /// hub other than its own). Reaching the operator's side needs their hub's own
    /// <see cref="IHubContext{THub}"/>, not this hub's <c>Clients</c>.
    /// </summary>
    private async Task BroadcastAsync(ConversationId conversationId, VisitorId visitorId, int sequence)
    {
        var page = await getHistory.HandleAsVisitorAsync(
            new GetConversationHistoryAsVisitor(conversationId, visitorId, sequence + 1, 1), Context.ConnectionAborted);
        var sentMessage = page.Value.Messages.Single();
        var dto = ToDto(sentMessage);
        var group = GroupName(conversationId);
        await Clients.Group(group).SendAsync("MessageReceived", dto, Context.ConnectionAborted);
        await operatorHub.Clients.Group(group).SendAsync("MessageReceived", dto, Context.ConnectionAborted);
    }

    internal static string GroupName(ConversationId conversationId) => $"conversation:{conversationId.Value}";

    private static MessageDto ToDto(Application.Abstractions.MessageHistoryItem item) =>
        new(item.Id.Value, item.Sequence, item.AuthorKind.ToString(), item.AuthorId, item.Body, item.CreatedAt);

    private static IReadOnlyList<MessageDto> ToDtos(IReadOnlyList<Application.Abstractions.MessageHistoryItem> items) =>
        [.. items.Select(ToDto)];
}
