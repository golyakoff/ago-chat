using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`25-04`: EF adapter for <see cref="IAiProcessingBasisDeclarationRepository"/> - shaped
/// exactly like <see cref="AcceptanceRepository"/>, insert plus one read, no update path at all.</summary>
public sealed class AiProcessingBasisDeclarationRepository(AgoChatDbContext db) : IAiProcessingBasisDeclarationRepository
{
    public async Task SaveAsync(AiProcessingBasisDeclaration declaration, CancellationToken cancellationToken)
    {
        db.AiProcessingBasisDeclarations.Add(declaration);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AiProcessingBasisDeclaration?> GetLatestForSiteAsync(
        SiteId siteId, CancellationToken cancellationToken) =>
        await db.AiProcessingBasisDeclarations
            .Where(d => d.SiteId == siteId)
            .OrderByDescending(d => d.DeclaredAt)
            .FirstOrDefaultAsync(cancellationToken);
}
