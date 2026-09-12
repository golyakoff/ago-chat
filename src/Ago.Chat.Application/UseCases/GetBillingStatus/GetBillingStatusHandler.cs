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
///
/// <para><b>`25-23`: three more reads joined the same lock-free shape</b> - the non-removed Administrator
/// count (<see cref="Ago.Chat.Application.Abstractions.IOperatorRoleRepository.GetNonRemovedHolderIdsAsync"/>,
/// deliberately not its row-locked sibling, see <see cref="BillingStatusDto"/>'s own remarks) and two
/// <see cref="Ago.Chat.Application.Abstractions.IPriceCatalogRepository.FindCurrentAsync"/> reads for the
/// seat-pricing keys `GetPricingForOwnerHandler` already reads the identical way. Six independent reads
/// now, not three; the same freshness reasoning above covers every one of them.</para>
/// </summary>
public sealed class GetBillingStatusHandler(
    ISiteRepository sites, IOperatorRepository operators, IBillingSubscriptionRepository subscriptions,
    IOperatorRoleRepository operatorRoles, IPriceCatalogRepository prices, IPermissionChecker permissions)
{
    // `25-23`: the identical bare literal `ChangeOperatorRoleHandler`/`AdministratorLimitEnforcer`/
    // `OperatorInviteRedemptionRepository` each already declare their own copy of - see those types'
    // own remarks for why there is still no shared named-role catalogue to read this from instead.
    private const string AdminRoleName = "Admin";

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
        // `23-86`: GetBaseForSiteAsync, not GetLatestForSiteAsync - this screen means "the site's base
        // subscription" (tier, seats), and with an option's own BillingSubscription rows now sharing
        // this table, the newest row for a site can be a channel bought yesterday rather than the tier
        // this screen is about. See IBillingSubscriptionRepository.GetBaseForSiteAsync's own remarks.
        var latest = await subscriptions.GetBaseForSiteAsync(query.SiteId, cancellationToken);

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

        // `25-23`: GetNonRemovedHolderIdsAsync, not CountNonRemovedHoldersAsync - see BillingStatusDto's
        // own remarks on AdminsUsed for why the locked sibling method is the wrong tool for a plain
        // display read.
        var adminHolderIds = await operatorRoles.GetNonRemovedHolderIdsAsync(query.SiteId, AdminRoleName, cancellationToken);

        // `25-23`: tier=="free" is the one bare-literal predicate this codebase already repeats for the
        // identical split (SubscriptionTierBands.ResolveAdminLimit, one line above the constant this
        // method reads two lines below) - matched here rather than invented as a differently-worded
        // second check.
        var tierDisplayName = site.Tier == "free" ? "Solo" : "Business";

        var basePrice = await prices.FindCurrentAsync(SubscriptionTierBands.BaseSeatPriceKey, cancellationToken);
        var extraPrice = await prices.FindCurrentAsync(SubscriptionTierBands.ExtraSeatPriceKey, cancellationToken);
        if (basePrice is null || extraPrice is null)
        {
            // `25-23`/`25-43`: the identical "unreachable on a deployment whose migration seed ran"
            // judgement GetPricingForOwnerHandler already makes for these same two keys - a tenant's
            // billing page must never be shown a fabricated seat price any more than the owner's
            // price-list screen may.
            throw new InvalidOperationException(
                "The billing status was read but the seat-pricing keys have no published version - "
                + "the migration seed that publishes them on this mechanism's first deploy did not run.");
        }

        var seatPricing = new BillingSeatPricingDto(
            SubscriptionTierBands.MinSeats,
            SubscriptionTierBands.MaxSeats,
            SubscriptionTierBands.BaseSeats,
            SubscriptionTierBands.FreeSeatsIncluded,
            basePrice.AmountRub,
            extraPrice.AmountRub,
            BillingSubscription.PeriodLength.TotalDays);

        // `25-23`: null, honestly, exactly when this key has never had a version published - see
        // BillingStatusDto's own remarks on AdminExtraPriceRub for why that is the ordinary state here,
        // unlike the two seat-pricing keys above.
        var adminExtraPrice = await prices.FindCurrentAsync(SubscriptionTierBands.AdminExtraPriceKey, cancellationToken);

        return new BillingStatusDto(
            site.Tier,
            site.SeatLimit,
            seatsUsed,
            latestDto,
            tierDisplayName,
            site.AdminLimit,
            adminHolderIds.Count,
            latest?.ExtraAdministratorsPurchased ?? 0,
            seatPricing,
            adminExtraPrice?.AmountRub);
    }
}
