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
}
