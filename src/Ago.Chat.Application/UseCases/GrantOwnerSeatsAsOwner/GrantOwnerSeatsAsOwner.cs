using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GrantOwnerSeatsAsOwner;

/// <summary>
/// `25-181`: the platform owner's own hand-granted extra - "site X gets Q more seats of role R,
/// beyond the tariff, because of Y". See <see cref="GrantOwnerSeatsAsOwnerHandler"/>'s own
/// remarks for why this carries no <see cref="Application.Abstractions.IPermissionChecker"/> check -
/// the identical "RequirePlatformOwner is the entire access-control story" shape every other owner
/// command in this codebase already follows.
///
/// <para><see cref="GrantedBy"/> is the platform owner's own Keycloak `sub` - recorded, never
/// authorising, the identical <see cref="RestoreOperatorSeatAsOwner.RestoreOperatorSeatAsOwner.RestoredBy"/>
/// shape.</para>
/// </summary>
/// <param name="SiteId">The tenant the extra is granted to.</param>
/// <param name="Role">Which capacity the extra counts toward.</param>
/// <param name="Quantity">1-5 - <see cref="Domain.OwnerSeatGrant.MinQuantity"/>/<see cref="Domain.OwnerSeatGrant.MaxQuantity"/>.</param>
/// <param name="GrantedBy">The platform owner's own Keycloak `sub`.</param>
/// <param name="Reason">Required, non-blank - the author's own explicit ask, for consistency with
/// <see cref="Domain.ModuleQuantityGrant.SetUnconditionalGrant"/>'s identical validation.</param>
/// <param name="ExpiresAt"><see langword="null"/> means indefinite ("бессрочно") - the identical
/// convention <see cref="Domain.ModuleQuantityGrant.UnconditionalGrantExpiresAt"/> already uses.</param>
public sealed record GrantOwnerSeatsAsOwner(
    SiteId SiteId, OwnerSeatGrantRole Role, int Quantity, string GrantedBy, string Reason, DateTimeOffset? ExpiresAt);
