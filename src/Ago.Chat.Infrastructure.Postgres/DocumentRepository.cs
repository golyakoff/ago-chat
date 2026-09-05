using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `24-02`. <see cref="GetByKeyAsync"/>/<see cref="SaveAsync"/> are the write path's own aggregate
/// load/save pair, the identical shape <c>ConversationRepository</c> already establishes for
/// <c>Conversation</c>/<c>Message</c> - <see cref="SaveAsync"/>'s own concurrency translation is a
/// direct copy of that class's own two <c>catch</c> blocks, restated for <see cref="Document"/>.
/// <see cref="FindVersionAsync"/>/<see cref="FindCurrentAsync"/> are the public read path's own direct
/// queries against <c>published_document_versions</c>, bypassing the aggregate entirely -
/// <see cref="IDocumentRepository"/>'s own remarks explain why both shapes live on the one port rather
/// than being split into two.
/// </summary>
public sealed class DocumentRepository(AgoChatDbContext db) : IDocumentRepository
{
    public Task<Document?> GetByKeyAsync(string documentKey, CancellationToken cancellationToken) =>
        db.Documents
            .Include("_versions")
            .FirstOrDefaultAsync(d => d.DocumentKey == documentKey, cancellationToken);

    public async Task SaveAsync(Document document, CancellationToken cancellationToken)
    {
        // A freshly Document.Create()'d document was never loaded through this context, so it is not
        // tracked yet - the same "detached means new" test ConversationRepository.SaveAsync's own
        // remarks give for Conversation.
        if (db.Entry(document).State == EntityState.Detached)
        {
            db.Documents.Add(document);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // `24-02`: the identical translation ConversationRepository.SaveAsync's own remarks explain
            // in full - clear the tracker so a caller's retry (PublishDocumentVersionHandler) actually
            // re-reads Postgres's current row instead of the identity map's stale, never-committed copy.
            db.ChangeTracker.Clear();
            throw new DocumentConcurrencyConflictException(document.Id);
        }
        // `24-02`: found live by PublishedDocumentIntegrationTests's own race test, the identical shape
        // ConversationRepository.SaveAsync's own second catch clause documents for `ix_conversation_assignments_open`
        // - EF's change tracker executes Added entities before Modified ones, so two publishes racing
        // over the same key can both compute the same next Sequence and the loser's new
        // PublishedDocumentVersion child row (an Added entity) hits ix_published_document_versions_key_sequence
        // before the parent Document's own stale-xmin UPDATE is ever attempted. Scoped to this one
        // constraint by name, not every unique violation this SaveChangesAsync could ever raise - the
        // same "translate exactly the constraint this call site can explain, nothing wider" precedent
        // that clause's own remarks state.
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "ix_published_document_versions_key_sequence",
        })
        {
            db.ChangeTracker.Clear();
            throw new DocumentConcurrencyConflictException(document.Id);
        }
    }

    public Task<PublishedDocumentVersion?> FindVersionAsync(
        string documentKey, string version, CancellationToken cancellationToken) =>
        db.PublishedDocumentVersions
            .FirstOrDefaultAsync(v => v.DocumentKey == documentKey && v.Version == version, cancellationToken);

    public Task<PublishedDocumentVersion?> FindCurrentAsync(string documentKey, CancellationToken cancellationToken) =>
        db.PublishedDocumentVersions
            .Where(v => v.DocumentKey == documentKey)
            .OrderByDescending(v => v.Sequence)
            .FirstOrDefaultAsync(cancellationToken);
}
