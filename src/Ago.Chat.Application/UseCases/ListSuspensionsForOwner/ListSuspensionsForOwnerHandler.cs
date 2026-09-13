using Ago.Chat.Application.Abstractions;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.ListSuspensionsForOwner;

/// <summary>`22-08`: the console's own "who is currently suspended" read - a thin wrapper over
/// <see cref="ISiteSuspensionReadStore.ListForOwnerAsync"/>, the same "a handler exists even for a
/// single-store read, so every use case this codebase resolves is a handler and not sometimes a bare
/// store" consistency <c>ListSitesForOwnerHandler</c> already keeps for its own analogous list. No
/// failure mode of its own - "no accounts currently suspended" is an empty list, not an error, the
/// identical reasoning <c>OwnerSitesEndpoints.HandleListSitesAsync</c>'s own remarks give for its
/// sibling list route.</summary>
public sealed class ListSuspensionsForOwnerHandler(ISiteSuspensionReadStore suspensions, IClock clock)
{
    public Task<IReadOnlyList<OwnerSuspensionSummary>> HandleAsync(
        ListSuspensionsForOwner query, CancellationToken cancellationToken) =>
        suspensions.ListForOwnerAsync(clock.UtcNow, cancellationToken);
}
