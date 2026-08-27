using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`14-02`: the EF adapter for <see cref="IChannelCredentialRepository"/> -
/// <see cref="WebhookEndpointRepository"/>'s own detached-means-insert shape.</summary>
public sealed class ChannelCredentialRepository(AgoChatDbContext db) : IChannelCredentialRepository
{
    public Task<ChannelCredential?> GetActiveAsync(SiteId siteId, ChannelKind kind, CancellationToken cancellationToken) =>
        db.ChannelCredentials
            .FirstOrDefaultAsync(c => c.SiteId == siteId && c.Kind == kind && c.Active, cancellationToken);

    public Task<ChannelCredential?> GetByIdAsync(ChannelCredentialId id, CancellationToken cancellationToken) =>
        db.ChannelCredentials.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public async Task<IReadOnlyList<ChannelCredential>> GetAllActiveAsync(ChannelKind kind, CancellationToken cancellationToken) =>
        await db.ChannelCredentials.Where(c => c.Kind == kind && c.Active).ToListAsync(cancellationToken);

    public async Task SaveAsync(ChannelCredential credential, CancellationToken cancellationToken)
    {
        if (db.Entry(credential).State == EntityState.Detached)
        {
            db.ChannelCredentials.Add(credential);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
