using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetBillingStatus;

/// <summary>
/// `13-04`: a plain read across two independent aggregates (<c>Site</c>'s own `Tier`/`SeatLimit`,
/// `BillingSubscription`'s own latest row) plus one derived count - no lock, no transaction, the same
/// "nothing here for a lock to protect" reasoning <see cref="Application.UseCases.GetSeatAssignmentSummary.GetSeatAssignmentSummaryHandler"/>'s
/// own remarks give for the analogous multi-read case. Whatever this call sees is exactly as fresh as
/// the moment each of its three reads ran; a write landing between them changes what the *next* call
/// reports, never this one - the correct behaviour for a console display, not a write decision
/// (`CLAUDE.md` rule 8 does not apply here: nothing this handler returns is compared-and-set against).
/// </summary>
public sealed class GetBillingStatusHandler(
    ISiteRepository sites, IOperatorRepository operators, IBillingSubscriptionRepository subscriptions, IPermissionChecker permissions)
{
    public async Task<Result<BillingStatusDto>> HandleAsync(GetBillingStatus query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to view this site's billing.");
        }

        var site = await sites.GetByIdAsync(query.SiteId, cancellationToken);
        if (site is null)
        {
            return ConversationErrors.SiteNotFound(query.SiteId.Value);
        }

        var seatsUsed = await operators.CountHeldSeatsAsync(query.SiteId, cancellationToken);
        var latest = await subscriptions.GetLatestForSiteAsync(query.SiteId, cancellationToken);

        var latestDto = latest is null
            ? null
            : new BillingSubscriptionSummaryDto(
                latest.Id.Value,
                latest.Status.ToString(),
                latest.RequestedSeats,
                latest.Tier,
                latest.CancelRequested,
                latest.CurrentPeriodEnd,
                latest.PendingSeatCount,
                latest.PendingTier);

        return new BillingStatusDto(site.Tier, site.SeatLimit, seatsUsed, latestDto);
    }
}
