using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `14-14`. Resolved by `RecordVisitorContactDetailHandler`/`ListVisitorContactDetailsHandler`/
/// `DeleteVisitorContactDetailHandler` only - no other handler in this codebase depends on
/// `IVisitorContactDetailRepository`, the concrete expression of that interface's own "structurally
/// incapable" remarks: this class shares no base type, no method, and no SQL with
/// <see cref="ChannelIdentityRepository"/>.
/// </summary>
public sealed class VisitorContactDetailRepository(AgoChatDbContext db) : IVisitorContactDetailRepository
{
    public async Task SaveAsync(VisitorContactDetail detail, CancellationToken cancellationToken)
    {
        // Contact details are never edited once recorded (no Rename-shaped method on
        // VisitorContactDetail), so the detached-vs-tracked branch WebhookEndpointRepository.SaveAsync
        // needs does not apply here - every call is a fresh insert, the identical shape
        // NoteRepository.SaveAsync already uses for the same reason.
        db.VisitorContactDetails.Add(detail);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<VisitorContactDetail>> GetForVisitorAsync(
        VisitorId visitorId, CancellationToken cancellationToken) =>
        await db.VisitorContactDetails
            .Where(d => d.VisitorId == visitorId)
            .OrderBy(d => d.RecordedAt)
            .ToListAsync(cancellationToken);

    public Task<VisitorContactDetail?> GetByIdAsync(VisitorContactDetailId id, CancellationToken cancellationToken) =>
        db.VisitorContactDetails.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

    public async Task DeleteAsync(VisitorContactDetail detail, CancellationToken cancellationToken)
    {
        db.VisitorContactDetails.Remove(detail);
        await db.SaveChangesAsync(cancellationToken);
    }
}
