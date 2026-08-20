using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeConversationRepository : IConversationRepository
{
    private readonly Dictionary<ConversationId, Conversation> _byId = [];

    public Task<Conversation?> GetByIdAsync(ConversationId id, CancellationToken cancellationToken) =>
        Task.FromResult(_byId.GetValueOrDefault(id));

    public Task<Conversation?> GetActiveForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken) =>
        Task.FromResult(_byId.Values.FirstOrDefault(
            c => c.VisitorId == visitorId && c.State != ConversationState.Closed));

    public Task SaveAsync(Conversation conversation, CancellationToken cancellationToken)
    {
        _byId[conversation.Id] = conversation;
        return Task.CompletedTask;
    }

    public void Seed(Conversation conversation) => _byId[conversation.Id] = conversation;
}
