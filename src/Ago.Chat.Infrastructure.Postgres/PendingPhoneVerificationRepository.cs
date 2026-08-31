using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`14-15`: the EF adapter for <see cref="IPendingPhoneVerificationRepository"/> - see that
/// port's own remarks for why one <see cref="SaveAsync"/> suffices here, unlike
/// <c>PendingChannelLinkRequestRepository</c>'s own two.</summary>
public sealed class PendingPhoneVerificationRepository(AgoChatDbContext db) : IPendingPhoneVerificationRepository
{
    public Task<PendingPhoneVerification?> GetByIdAsync(PendingPhoneVerificationId id, CancellationToken cancellationToken) =>
        db.PendingPhoneVerifications.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public async Task SaveAsync(PendingPhoneVerification verification, CancellationToken cancellationToken)
    {
        if (db.Entry(verification).State == EntityState.Detached)
        {
            db.PendingPhoneVerifications.Add(verification);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
