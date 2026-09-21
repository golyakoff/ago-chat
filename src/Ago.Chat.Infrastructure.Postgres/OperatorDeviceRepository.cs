using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

public sealed class OperatorDeviceRepository(AgoChatDbContext db) : IOperatorDeviceRepository
{
    public Task<OperatorDevice?> FindAsync(OperatorId operatorId, string installationId, CancellationToken cancellationToken) =>
        db.OperatorDevices.FirstOrDefaultAsync(
            d => d.OperatorId == operatorId && d.InstallationId == installationId, cancellationToken);

    public Task<OperatorDevice?> FindActiveByTokenAsync(PushProvider provider, string token, CancellationToken cancellationToken) =>
        db.OperatorDevices.FirstOrDefaultAsync(
            d => d.Provider == provider && d.Token == token && d.RevokedAt == null, cancellationToken);

    public async Task<IReadOnlyList<OperatorDevice>> ListActiveForOperatorAsync(
        OperatorId operatorId, CancellationToken cancellationToken) =>
        await db.OperatorDevices
            .Where(d => d.OperatorId == operatorId && d.RevokedAt == null)
            .ToListAsync(cancellationToken);

    public async Task SaveAsync(OperatorDevice device, CancellationToken cancellationToken)
    {
        // Same detached-vs-tracked check as WebhookEndpointRepository.SaveAsync - a freshly Register()'d
        // device was never loaded through this context.
        if (db.Entry(device).State == EntityState.Detached)
        {
            db.OperatorDevices.Add(device);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
