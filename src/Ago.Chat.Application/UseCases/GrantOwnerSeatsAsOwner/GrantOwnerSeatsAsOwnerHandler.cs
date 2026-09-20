using Ago.Chat.Application.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GrantOwnerSeatsAsOwner;

/// <summary>
/// `25-181`: writes the platform owner's own hand-granted extra - see <see cref="Domain.OwnerSeatGrant"/>'s
/// own remarks for what the grant is and why it is a second, independent row rather than a write to
/// <see cref="Domain.Site.SeatLimit"/>/<see cref="Domain.Site.AdminLimit"/> directly.
///
/// <para><b>No <see cref="IPermissionChecker"/> call</b> - the identical reason every other owner
/// surface in this codebase gives (<see cref="UseCases.SetUnconditionalModuleGrantAsOwner.SetUnconditionalModuleGrantAsOwnerHandler"/>'s
/// own remarks, unchanged): the fact that authorizes this call is the `RequirePlatformOwner` policy on
/// the route that resolves this handler, which does not live in a table this checker could
/// query.</para>
///
/// <para><b>Reason and quantity are validated here, before the store is ever touched</b> - the
/// identical "reject the caller's input before touching another system" ordering
/// <see cref="RevokeModuleForSiteAsOwner.RevokeModuleForSiteAsOwnerHandler"/>'s own force/reason guard
/// already follows. <see cref="Domain.OwnerSeatGrant.SetGrant"/> would also refuse an out-of-range
/// quantity or a blank reason on its own, but that throws an exception - checking here first turns the
/// caller's own mistake into a proper `400` <see cref="Result"/>, the same split every other owner
/// command in this file draws between "caller's mistake" (checked here) and "should have been
/// impossible" (left to throw).</para>
/// </summary>
public sealed class GrantOwnerSeatsAsOwnerHandler(IOwnerSeatGrantStore grants, ISiteRepository sites, IClock clock)
{
    /// <summary>The identical bound <see cref="RevokeModuleForSiteAsOwner.RevokeModuleForSiteAsOwnerHandler.MaxReasonLength"/>
    /// already uses, reused rather than reinvented for the same shape of thing (a person typing a
    /// free-text justification for an owner-only override).</summary>
    public const int MaxReasonLength = RevokeModuleForSiteAsOwner.RevokeModuleForSiteAsOwnerHandler.MaxReasonLength;

    public async Task<Result> HandleAsync(GrantOwnerSeatsAsOwner command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Reason))
        {
            return ConversationErrors.OwnerSeatGrantReasonRequired(
                "A reason is required whenever the platform owner grants a tenant extra seats - state why.");
        }

        if (command.Reason.Trim().Length > MaxReasonLength)
        {
            return ConversationErrors.OwnerSeatGrantReasonRequired(
                $"An owner seat grant reason cannot exceed {MaxReasonLength} characters.");
        }

        if (command.Quantity < Domain.OwnerSeatGrant.MinQuantity || command.Quantity > Domain.OwnerSeatGrant.MaxQuantity)
        {
            return ConversationErrors.OwnerSeatGrantQuantityInvalid(command.Quantity);
        }

        var site = await sites.GetByIdAsync(command.SiteId, cancellationToken);
        if (site is null)
        {
            return ConversationErrors.SiteNotFound(command.SiteId.Value);
        }

        await grants.GrantAsync(
            command.SiteId, command.Role, command.Quantity, command.GrantedBy, command.Reason.Trim(), clock.UtcNow,
            command.ExpiresAt, cancellationToken);

        return Result.Success();
    }
}
