using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ListNonEntitledChannelCredentialsAsOwner;

/// <summary>
/// `23-85`/`adr/0151`: every active channel credential, across every tenant and every
/// <see cref="ChannelKind"/>, whose account currently holds no entitlement for that kind - the
/// platform owner's own diagnostic read, gated by `RequirePlatformOwner` at the route (no
/// <see cref="Application.Abstractions.IPermissionChecker"/> call here, the identical single-gate
/// shape every other owner-only handler in this codebase already uses).
///
/// <para><b>Composed from existing ports, not a new Dapper read store.</b> `caching.md`'s own
/// reasoning for a dedicated read store is a <em>hot</em> path read many times per second
/// (<see cref="IEnabledModuleReadStore"/>'s own remarks: the message pipeline, on every visitor
/// message). This is the opposite: an owner-triggered diagnostic, run by hand, rarely - the
/// walkthrough this item's own hard requirement demands happens once before the mechanism is trusted,
/// and occasionally afterward for a new lapse. Iterating <see cref="ChannelKind"/> (seven values today)
/// and calling <see cref="IChannelCredentialRepository.GetAllActiveAsync"/> once per kind, then
/// <see cref="ChannelEntitlement.IsEntitledAsync"/> once per active row, is <c>O(kinds × credentials)</c>
/// sequential awaits - fine for a bounded, infrequent admin read against a dataset this deployment's
/// own commercial state keeps small (`23-86`'s own "nobody has bought an option yet"), and building a
/// purpose-made read store for a query this rarely run would be exactly the premature optimisation
/// `clean-architecture.md`'s own qualifying rules warn a platform layer against.</para>
/// </summary>
public sealed class ListNonEntitledChannelCredentialsAsOwnerHandler(
    IChannelCredentialRepository credentials, IBillingOptionEntitlementProvider entitlements,
    IModuleQuantityGrantStore grants)
{
    private static readonly IReadOnlyList<ChannelKind> AllKinds = Enum.GetValues<ChannelKind>();

    public async Task<IReadOnlyList<NonEntitledChannelCredentialSummary>> HandleAsync(
        ListNonEntitledChannelCredentialsAsOwner query, CancellationToken cancellationToken)
    {
        var result = new List<NonEntitledChannelCredentialSummary>();

        foreach (var kind in AllKinds)
        {
            var activeForKind = await credentials.GetAllActiveAsync(kind, cancellationToken);
            foreach (var credential in activeForKind)
            {
                var entitled = await ChannelEntitlement.IsEntitledAsync(
                    entitlements, grants, credential.SiteId, kind, cancellationToken);
                if (!entitled)
                {
                    result.Add(new NonEntitledChannelCredentialSummary(
                        credential.Id, credential.SiteId, kind, credential.CreatedAt));
                }
            }
        }

        return result;
    }
}
