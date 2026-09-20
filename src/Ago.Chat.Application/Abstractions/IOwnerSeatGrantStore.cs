using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>`25-181`: the platform owner's own hand-granted seat extras - see <see cref="OwnerSeatGrant"/>'s
/// own remarks for why this is a second, independent row rather than a fifth constructor parameter on
/// <see cref="Site.ActivateSubscription"/>. One implementation
/// (<c>Ago.Chat.Infrastructure.Postgres.OwnerSeatGrantStore</c>), the identical "get-or-create, mutate,
/// save" shape <see cref="IModuleQuantityGrantStore"/> already establishes for its own (site, module)
/// grant - deliberately this thin: every caller that needs to *act* on the live number
/// (<c>Application.UseCases.OperatorRoleSeats.OperatorRoleSeatReconciler</c>'s own extra-capacity
/// parameter, <c>GetOwnerSeatSummary.GetOwnerSeatSummaryHandler</c>) already has
/// <see cref="ISiteRepository"/>/<see cref="IOperatorRoleRepository"/> for the base limit and live
/// count, so this port's only job is the one fact neither of those already knows: what the owner has
/// granted, right now, against <paramref name="now"/>.</summary>
public interface IOwnerSeatGrantStore
{
    /// <summary>The live, caller-clocked extra for (<paramref name="siteId"/>, <paramref name="role"/>) -
    /// <c>0</c> if nothing was ever granted, or if the grant that was has since expired
    /// (<see cref="OwnerSeatGrant.EffectiveQuantity"/>). Never cached - CLAUDE.md rule 8: a role's own
    /// current capacity is a write decision, so this is read fresh every time, exactly the posture
    /// <see cref="IModuleQuantityGrantStore.GetQuantityAsync"/> already takes for its own analogous
    /// grant.</summary>
    Task<int> GetEffectiveExtraAsync(SiteId siteId, OwnerSeatGrantRole role, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Sets or replaces the current owner-granted extra for (<paramref name="siteId"/>,
    /// <paramref name="role"/>) - a snapshot, never a delta (<see cref="OwnerSeatGrant"/>'s own
    /// remarks).</summary>
    Task GrantAsync(
        SiteId siteId, OwnerSeatGrantRole role, int quantity, string grantedBy, string reason,
        DateTimeOffset now, DateTimeOffset? expiresAt, CancellationToken cancellationToken);
}
