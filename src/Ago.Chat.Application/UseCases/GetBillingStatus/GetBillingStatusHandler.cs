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
///
/// <para><b>`26-295`: two more reads joined the same lock-free shape</b> -
/// <see cref="Ago.Chat.Application.Abstractions.IBillingSubscriptionRepository.ListOptionsForSiteAsync"/>
/// (the site's own channel add-on rows) and one more <c>FindCurrentAsync</c> for
/// <see cref="Ago.Chat.Domain.ChannelAddOnPricing.ChannelAddOnKey"/> - see <see cref="BillingStatusDto"/>'s
/// own remarks on <c>ChannelCount</c>/<c>ChannelAddOnPriceRub</c>/<c>NextChargeRub</c> for what they
/// answer. The identical freshness reasoning above covers these too: a write landing between this read
/// and the next call changes what that <i>next</i> call reports, never this one.</para>
/// </summary>
public sealed class GetBillingStatusHandler(
    ISiteRepository sites, IBillingSubscriptionRepository subscriptions,
    IOperatorRoleRepository operatorRoles, IPriceCatalogRepository prices, IPermissionChecker permissions)
{
    // `25-23`: the identical bare literal `ChangeOperatorRoleHandler`/
    // `OperatorInviteRedemptionRepository` each already declare their own copy of - see those types'
    // own remarks for why there is still no shared named-role catalogue to read this from instead.
    private const string AdminRoleName = "Admin";

    // `25-170`: the seeded Operator role's own literal, the counterpart of AdminRoleName right above -
    // this handler's own SeatsUsed is now the Operator role's own held-seat count, not
    // Domain.Operator.HoldsSeat (removed by this item).
    private const string OperatorRoleName = "Operator";

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

        // `25-170`: IOperatorRoleRepository.GetHeldSeatHolderIdsAsync, not
        // IOperatorRepository.CountHeldSeatsAsync (removed by this item along with
        // Domain.Operator.HoldsSeat) - "holds a seat" is now a fact about the Operator role's own
        // (operator, role) pairing, not the operator account.
        var seatHolderIds = await operatorRoles.GetHeldSeatHolderIdsAsync(query.SiteId, OperatorRoleName, cancellationToken);
        var seatsUsed = seatHolderIds.Count;
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
                latest.PendingTier,
                latest.PendingAdminCount);

        // `25-23`/`25-170`: GetHeldSeatHolderIdsAsync, not LockAndGetHeldSeatHolderIdsAsync - see
        // BillingStatusDto's own remarks on AdminsUsed for why the locked sibling method is the wrong
        // tool for a plain display read. Now counts the Admin role's own held seats specifically
        // (HoldsSeat = true), not merely who holds the role - the same "how many count against the
        // limit right now" question SeatsUsed above already answers for the Operator role.
        var adminHolderIds = await operatorRoles.GetHeldSeatHolderIdsAsync(query.SiteId, AdminRoleName, cancellationToken);

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

        // `26-295`: every option row for the site, narrowed to the ones ChannelAddOnPricing.PriceKeyFor
        // recognises as a channel add-on (rather than re-typing the "channel-" prefix literal here) and
        // currently Succeeded - a Pending/Failed/Lapsed option is not "connected". Reused for both
        // ChannelCount and the channelAmount component of NextChargeRub below, so the two can never
        // report a different count of the same thing.
        var options = await subscriptions.ListOptionsForSiteAsync(query.SiteId, cancellationToken);
        var connectedChannelOptions = options
            .Where(o => o.Status == BillingSubscriptionStatus.Succeeded && ChannelAddOnPricing.PriceKeyFor(o.OptionKey!.Value) is not null)
            .ToList();
        var connectedChannelCount = connectedChannelOptions.Count;
        // `26-299`: the actual channel kinds, not merely the count - GetBillingStatus's own
        // ConnectedChannels remarks state why the console needs this for its per-kind renew toggles.
        // TryResolveChannelKind, not the throwing form - an option key this codebase's own tests (and,
        // in principle, a historical/retired ChannelKind) seed without a real matching kind is simply
        // left out of this per-kind list rather than failing the whole read; ChannelCount/NextChargeRub
        // below are computed from connectedChannelOptions directly so they never depend on whether every
        // row happens to resolve.
        var connectedChannels = connectedChannelOptions
            .Select(o => (Option: o, Kind: TryResolveChannelKind(o.OptionKey!.Value)))
            .Where(x => x.Kind is not null)
            // `26-304`: ToString() here, not the bare enum - BillingConnectedChannelDto.Kind's own wire
            // shape is now the member-name string every other ChannelKind-on-wire DTO already uses.
            .Select(x => new BillingConnectedChannelDto(x.Kind!.Value.ToString(), x.Option.Id.Value, x.Option.CancelRequested, x.Option.CurrentPeriodEnd))
            .ToList();
        var channelAddOnPrice = await prices.FindCurrentAsync(ChannelAddOnPricing.ChannelAddOnKey, cancellationToken);

        // `26-299`: what will actually renew next period, for NextChargeRub below - a channel option
        // already flagged CancelRequested will not renew, so it drops out of the recurring total (see
        // BillingStatusDto's own NextChargeRub remarks). ChannelCount/ConnectedChannels above stay
        // "currently connected" regardless of CancelRequested - a different, still-true fact. Computed
        // from connectedChannelOptions, not the (possibly narrower) connectedChannels DTO list above -
        // the recurring total must count every connected channel that will renew, whether or not this
        // read could also resolve its own ChannelKind for display.
        var renewingChannelCount = connectedChannelOptions.Count(o => !o.CancelRequested);

        // `26-299`: the composition this subscription will actually be charged for next - its own
        // scheduled pending values when set (SetNextPeriodComposition's own write), else whatever is
        // billing today. See BillingStatusDto's own NextChargeRub remarks for why this must not be the
        // currently-billing composition once a change is already scheduled.
        var effectiveSeats = latest?.PendingSeatCount ?? latest?.RequestedSeats ?? 0;
        var effectiveExtraAdministrators = latest?.PendingAdminCount ?? latest?.ExtraAdministratorsPurchased ?? 0;

        // `26-295`: see BillingStatusDto's own remarks on NextChargeRub for the full reasoning - null
        // whenever there is no base subscription currently expected to renew, or a component this total
        // needs has no currently-published price to read honestly.
        decimal? nextChargeRub = null;
        if (latest is not null && latest.Status is BillingSubscriptionStatus.Succeeded or BillingSubscriptionStatus.PastDue)
        {
            var adminUnpriced = effectiveExtraAdministrators > 0 && adminExtraPrice is null;
            var channelsUnpriced = renewingChannelCount > 0 && channelAddOnPrice is null;
            if (!adminUnpriced && !channelsUnpriced)
            {
                var seatAmount = SubscriptionTierBands.ComputeSeatPriceRub(effectiveSeats, basePrice.AmountRub, extraPrice.AmountRub);
                var adminAmount = effectiveExtraAdministrators * (adminExtraPrice?.AmountRub ?? 0m);
                var channelAmount = renewingChannelCount * (channelAddOnPrice?.AmountRub ?? 0m);
                nextChargeRub = seatAmount + adminAmount + channelAmount;
            }
        }

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
            adminExtraPrice?.AmountRub,
            connectedChannelCount,
            channelAddOnPrice?.AmountRub,
            nextChargeRub,
            latest?.PaymentMethodId is { Length: > 0 },
            connectedChannels);
    }

    /// <summary>`26-299`: <see cref="Ago.Chat.Domain.ChannelEntitlementOptionKeys.For"/>'s own reverse
    /// direction - that method only ever maps <see cref="ChannelKind"/> -&gt; <see cref="BillingOptionKey"/>,
    /// so a display read that already has the option key (from a stored row) needs this small inverse to
    /// recover which kind it names. A linear scan over <see cref="ChannelKind"/>'s own handful of members,
    /// not a stored reverse map - this runs once per connected channel on one site's own billing-status
    /// read, not a hot path. <see langword="null"/>, not a throw, when nothing matches - every real
    /// `channel-*` key this codebase ever writes originates from <see cref="ChannelEntitlementOptionKeys.For"/>
    /// itself (<c>PurchaseChannelAddOnHandler</c>'s own call) and always resolves, but a synthetic key
    /// (a test double, or a future retired <see cref="ChannelKind"/>) must not fail this whole read over
    /// one row it cannot name a kind for - see this method's own caller for why <c>ChannelCount</c>/
    /// <c>NextChargeRub</c> are computed independently of whether every row resolves here.</summary>
    private static ChannelKind? TryResolveChannelKind(BillingOptionKey optionKey)
    {
        foreach (var kind in Enum.GetValues<ChannelKind>())
        {
            if (ChannelEntitlementOptionKeys.For(kind) == optionKey)
            {
                return kind;
            }
        }

        return null;
    }
}
