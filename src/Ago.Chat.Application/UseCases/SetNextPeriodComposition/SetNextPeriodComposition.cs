using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.SetNextPeriodComposition;

/// <summary>
/// `26-299`: `POST /api/v1/sites/{siteId}/billing/subscriptions/{subscriptionId}/next-period`'s own
/// command - the console billing-v2 screen's "Card B" write, planning the base subscription's whole
/// next-period shape (seats and extra Administrators) in one call rather than composing several
/// single-dimension ones. Gated by the identical <see cref="Domain.Permission.SiteConfigure"/> every
/// other billing write in this codebase already requires.
///
/// <para><b>Base subscriptions only - the identical restriction `PurchaseChannelAddOnHandler`'s own
/// base-subscription guard already states for the analogous distinction.</b> An option row (a connected
/// channel) has no seat count or Administrator ceiling of its own to plan ahead for.</para>
///
/// <para><b>Channel renewal is deliberately out of this command's scope, and worth stating why.</b> The
/// console's own toggle for "stop renewing channel X" already has a real, working endpoint underneath it:
/// <c>CancelSubscriptionHandler</c> already accepts any <see cref="BillingSubscription"/> id, base or
/// option, and a channel option's own <see cref="BillingSubscription.CancelRequested"/> flag already
/// governs whether it renews - no new mechanism was needed for that direction. The other direction -
/// "start renewing a channel I have not yet bought, for free, starting next period" - is a materially
/// different feature (a deferred first-time purchase with no charge today, that must charge itself
/// automatically at a future, uncertain date) that this item's own report flags as deferred rather than
/// improvised here; see that report for the reasoning in full.</para>
/// </summary>
public sealed record SetNextPeriodComposition(
    OperatorId RequestedBy, SiteId SiteId, BillingSubscriptionId SubscriptionId, int RequestedSeats, int RequestedExtraAdministrators);

/// <summary>Echoes back what was actually recorded, resolved against
/// <see cref="Domain.SubscriptionTierBands.TryResolveTier"/> - the console's own confirmation that the
/// composition it asked to schedule is the one this codebase will actually apply at the next
/// renewal.</summary>
public sealed record SetNextPeriodCompositionResult(string Tier, int RequestedSeats, int RequestedExtraAdministrators);
