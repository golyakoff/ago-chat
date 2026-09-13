using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

public sealed class AttachmentRepository(AgoChatDbContext db) : IAttachmentRepository
{
    public Task<Attachment?> GetByIdAsync(AttachmentId id, CancellationToken cancellationToken) =>
        db.Attachments.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public async Task SaveAsync(Attachment attachment, CancellationToken cancellationToken)
    {
        // Same detached-vs-tracked check as ConversationRepository.SaveAsync - a freshly
        // CreatePending()'d attachment was never loaded through this context.
        if (db.Entry(attachment).State == EntityState.Detached)
        {
            db.Attachments.Add(attachment);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>`23-76`: backed by `ix_attachments_site_content_hash` (site_id, content_hash, filtered
    /// to `state = 'Ready' AND content_hash IS NOT NULL`) - see that index's own remarks. `AsNoTracking`:
    /// this is a read-only lookup the caller only ever reads fields off (never mutates and saves back
    /// through this instance - it mutates its *own* attachment, loaded separately via
    /// <see cref="GetByIdAsync"/>), so there is nothing for the change tracker to buy here.</summary>
    public Task<Attachment?> FindReadyDuplicateAsync(
        SiteId siteId, string contentHash, AttachmentId excluding, CancellationToken cancellationToken) =>
        db.Attachments
            .AsNoTracking()
            .Where(a =>
                a.SiteId == siteId && a.ContentHash == contentHash && a.State == AttachmentState.Ready && a.Id != excluding)
            .OrderBy(a => a.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
}
