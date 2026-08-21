using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

public sealed class SiteRepository(AgoChatDbContext db) : ISiteRepository
{
    public Task<Site?> GetByPublicKeyAsync(string publicKey, CancellationToken cancellationToken) =>
        db.Sites.FirstOrDefaultAsync(s => s.PublicKey == publicKey, cancellationToken);
}
