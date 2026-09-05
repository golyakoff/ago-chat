using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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
        // `23-04`: a second translated shape, found live by AssignConversationConcurrencyTests - not
        // anticipated by 6-08's own design. Two operators racing to take the same Waiting conversation
        // both stage a new open ConversationAssignmentInterval (IConversationAssignmentLog.Open) on
        // this same SaveChangesAsync before the conversation's own row is even touched - EF's change
        // tracker executes Added entities before Modified ones, so the loser can hit 23-03's own
        // "at most one open interval" partial unique index (ix_conversation_assignments_open) before
        // ever reaching the xmin check this catch block above already guards. That is a real Postgres
        // 23505, not a DbUpdateConcurrencyException, so it reached HandleAsync's retry loop untranslated
        // until this clause existed - an operator would have seen a raw 500 for losing a take, not
        // Conversation.InvalidState. Scoped to this one constraint by name, not every unique violation
        // this SaveChangesAsync could ever raise on this shared DbContext (a genuine duplicate
        // client_message_id, for instance, means something else entirely and must not be retried the
        // same way) - the same "translate exactly the constraint this call site can explain, nothing
        // wider" precedent TagRepository/SiteRegistrationRepository/OperatorInviteRedemptionRepository
        // already set for their own single-purpose unique-violation catches. Same ChangeTracker.Clear()
        // reasoning as the block above: a caller's retry must re-read the truth, not the identity map's
        // stale copy of a row whose save never actually committed.
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "ix_conversation_assignments_open",
        })
        {
            db.ChangeTracker.Clear();
            throw new ConversationConcurrencyConflictException(conversation.Id);
        }
    }
}
