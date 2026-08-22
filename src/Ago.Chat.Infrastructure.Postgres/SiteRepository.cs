using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

public sealed class SiteRepository(AgoChatDbContext db) : ISiteRepository
{
    public Task<Site?> GetByPublicKeyAsync(string publicKey, CancellationToken cancellationToken) =>
        db.Sites.FirstOrDefaultAsync(s => s.PublicKey == publicKey, cancellationToken);

    public Task<Site?> GetByIdAsync(SiteId id, CancellationToken cancellationToken) =>
        db.Sites.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    // EF.Property against the shadow-mapped backing field (SiteConfiguration.cs) - Site.AllowedOrigins
    // itself is Ignore()'d for mapping purposes, same reason GetByPublicKeyAsync above cannot select
    // through it either. Translates to `WHERE @origin = ANY(allowed_origins)` against the native
    // Postgres array column.
    public Task<bool> AnyAllowsOriginAsync(string origin, CancellationToken cancellationToken) =>
        db.Sites.AnyAsync(s => EF.Property<List<string>>(s, "_allowedOrigins").Contains(origin), cancellationToken);
}
