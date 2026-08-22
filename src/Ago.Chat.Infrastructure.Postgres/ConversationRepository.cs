using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

public sealed class ConversationRepository(AgoChatDbContext db) : IConversationRepository
{
    public Task<Conversation?> GetByIdAsync(ConversationId id, CancellationToken cancellationToken) =>
        db.Conversations
            .Include("_messages")
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public Task<Conversation?> GetActiveForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken) =>
        db.Conversations
            .Include("_messages")
            .FirstOrDefaultAsync(c => c.VisitorId == visitorId && c.State != ConversationState.Closed, cancellationToken);

    public async Task<IReadOnlyList<Conversation>> GetAssignedToOperatorAsync(OperatorId operatorId, CancellationToken cancellationToken) =>
        await db.Conversations
            .Include("_messages")
            .Where(c => c.OperatorId == operatorId && c.State == ConversationState.Assigned)
            .ToListAsync(cancellationToken);

    public async Task SaveAsync(Conversation conversation, CancellationToken cancellationToken)
    {
        // A freshly-Start()'d conversation was never loaded through this context, so it is not
        // tracked yet; one that came from GetByIdAsync/GetActiveForVisitorAsync already is, and its
        // mutations (including messages added to the tracked _messages collection) are picked up by
        // SaveChangesAsync with no explicit Update() call needed.
        if (db.Entry(conversation).State == EntityState.Detached)
        {
            db.Conversations.Add(conversation);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
