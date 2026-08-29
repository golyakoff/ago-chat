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
            .Include("_moduleTasks")
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public Task<Conversation?> GetActiveForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken) =>
        db.Conversations
            .Include("_messages")
            .Include("_moduleTasks")
            .FirstOrDefaultAsync(c => c.VisitorId == visitorId && c.State != ConversationState.Closed, cancellationToken);

    public async Task<IReadOnlyList<Conversation>> GetAssignedToOperatorAsync(OperatorId operatorId, CancellationToken cancellationToken) =>
        await db.Conversations
            .Include("_messages")
            .Include("_moduleTasks")
            .Where(c => c.OperatorId == operatorId && c.State == ConversationState.Assigned)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Conversation>> GetWaitingForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
        await db.Conversations
            .Include("_messages")
            .Include("_moduleTasks")
            .Where(c => c.SiteId == siteId && c.State == ConversationState.Waiting)
            .OrderBy(c => c.CreatedAt)
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

        // `6-08`: translated here, not left to propagate as EF's own type - IConversationRepository's
        // own remarks (and ConversationConcurrencyConflictException's) explain why the port's contract
        // is a technology-agnostic exception rather than Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException;
        // this adapter is the one place in the whole call chain allowed to know which ORM raised it.
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A failed SaveChangesAsync does not untrack anything - this conversation stays in the
            // change tracker with our own now-untrustworthy local edit, and the outbox row the same
            // handler staged in this same failed attempt stays tracked as a pending insert that never
            // actually committed (the whole transaction rolled back with it). Left alone, a caller that
            // catches ConversationConcurrencyConflictException and calls GetByIdAsync again on this same
            // DbContext would hit the identity map and get back these same stale instances rather than
            // Postgres's current row - silently defeating the entire point of a reload-and-retry.
            // Clear() is what makes a caller's retry actually re-read the truth, and what stops the
            // stale outbox row from riding along into whatever SaveChangesAsync call comes next.
            db.ChangeTracker.Clear();
            throw new ConversationConcurrencyConflictException(conversation.Id);
        }
    }
}
