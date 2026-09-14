using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`25-04`: an in-memory <see cref="IAiProcessingBasisDeclarationRepository"/> - insert-only,
/// like the real one, so a test cannot accidentally prove something the production adapter could not
/// do. <see cref="Saved"/> is exposed so a test can assert *how many* declarations exist and that each
/// carries its own author and instant, which is the separateness the item demands.</summary>
public sealed class FakeAiProcessingBasisDeclarationRepository : IAiProcessingBasisDeclarationRepository
{
    public List<AiProcessingBasisDeclaration> Saved { get; } = [];

    public Task SaveAsync(AiProcessingBasisDeclaration declaration, CancellationToken cancellationToken)
    {
        Saved.Add(declaration);
        return Task.CompletedTask;
    }

    public Task<AiProcessingBasisDeclaration?> GetLatestForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
        Task.FromResult(Saved
            .Where(d => d.SiteId == siteId)
            .OrderByDescending(d => d.DeclaredAt)
            .FirstOrDefault());
}
