using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `25-04`: the write-side port for <see cref="AiProcessingBasisDeclaration"/> - deliberately shaped
/// exactly like <see cref="IAcceptanceRepository"/> (insert plus a read-back by subject, no update, no
/// delete) because the two hold the same *kind* of thing: evidence that somebody stated something at a
/// moment in time. Keeping the two ports separate rather than widening
/// <see cref="IAcceptanceRepository"/> with a second record type is the port-level half of the
/// distinction <see cref="AiProcessingBasisDeclaration"/>'s own remarks argue for - one port that could
/// write either fact is one refactor away from a caller writing the wrong one.
/// </summary>
public interface IAiProcessingBasisDeclarationRepository
{
    /// <summary>Always an insert, never a lookup-then-update - <see cref="IAcceptanceRepository.SaveAsync"/>'s
    /// own remarks, restated: a second declaration is a second row.</summary>
    Task SaveAsync(AiProcessingBasisDeclaration declaration, CancellationToken cancellationToken);

    /// <summary>The site's most recent declaration, or <see langword="null"/> if it has never made one -
    /// what <c>EnableAiAddOnHandler</c> checks, and what the console's own status screen renders as
    /// "declared by X on Y".</summary>
    Task<AiProcessingBasisDeclaration?> GetLatestForSiteAsync(SiteId siteId, CancellationToken cancellationToken);
}
