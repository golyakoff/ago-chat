using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

public sealed class WebhookEndpointRepository(AgoChatDbContext db) : IWebhookEndpointRepository
{
    public Task<WebhookEndpoint?> GetByIdAsync(WebhookEndpointId id, CancellationToken cancellationToken) =>
        db.WebhookEndpoints.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public async Task<IReadOnlyList<WebhookEndpoint>> GetAllForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
        await db.WebhookEndpoints
            .Where(e => e.SiteId == siteId)
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task SaveAsync(WebhookEndpoint endpoint, CancellationToken cancellationToken)
    {
        // Same detached-vs-tracked check as AttachmentRepository.SaveAsync - a freshly Register()'d
        // endpoint was never loaded through this context.
        if (db.Entry(endpoint).State == EntityState.Detached)
        {
            db.WebhookEndpoints.Add(endpoint);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
