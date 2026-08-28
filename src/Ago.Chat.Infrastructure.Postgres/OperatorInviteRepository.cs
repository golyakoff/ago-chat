using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

public sealed class OperatorInviteRepository(AgoChatDbContext db) : IOperatorInviteRepository
{
    public async Task SaveAsync(OperatorInvite invite, CancellationToken cancellationToken)
    {
        if (db.Entry(invite).State == EntityState.Detached)
        {
            db.OperatorInvites.Add(invite);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
