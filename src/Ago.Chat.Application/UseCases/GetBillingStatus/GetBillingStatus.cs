using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetBillingStatus;

/// <summary>`13-04`: the console billing screen's own bootstrap read - a real gap found while building
/// that screen, not anticipated by `13-01`/`13-02`/`13-03`'s own Scope (all three built write paths;
/// none built the corresponding read `GET /api/v1/sites/{siteId}` never existed for any purpose,
/// `SitesEndpoints.HandleRegisterSiteAsync`'s own remarks already name that exact gap for a different
/// reason). Gated by <see cref="Domain.Permission.SiteConfigure"/> - the identical permission every
/// other billing write endpoint (`13-02`'s checkout, `13-03`'s cancel/seat-change) already requires for
/// a billing/tier decision, so a caller who cannot act on this screen cannot see it either.</summary>
public sealed record GetBillingStatus(OperatorId RequestedBy, SiteId SiteId);

/// <summary><paramref name="SeatsUsed"/> is <see cref="Ago.Chat.Application.Abstractions.IOperatorRepository.CountHeldSeatsAsync"/>'s
/// own count - operators actually holding an assigned seat right now
/// (<c>HoldsSeat AND RemovedAt IS NULL</c>), the same number `GetSeatAssignmentSummaryHandler`'s
/// `HeldSeats` already answers for the operator-management screen, reused here rather than a second,
/// differently-worded count of the identical thing. <paramref name="LatestSubscription"/> is
/// <see langword="null"/> only for a site that has never started a checkout - still free by
/// construction.</summary>
public sealed record BillingStatusDto(string Tier, int SeatLimit, int SeatsUsed, BillingSubscriptionSummaryDto? LatestSubscription);

/// <summary>
/// The most recent <see cref="BillingSubscription"/> row for the site, carried across the wire as
/// plain values - <paramref name="Status"/> is <see cref="BillingSubscriptionStatus"/>'s own member
/// name via <c>ToString()</c>, the same "a client reads a name, not an ordinal" precedent
/// <c>SitesEndpoints.SiteExportStatusResponse</c> already established for an analogous status enum on
/// the wire. <b>This is deliberately the console's own honest-pending-state signal</b>: a
/// <see cref="Status"/> of <c>"Pending"</c> is what the screen polls after returning from ЮKassa's
/// hosted checkout, and only a transition away from <c>"Pending"</c> - to <c>"Succeeded"</c> or
/// <c>"Failed"</c> - is ever shown as a settled outcome, never the redirect return alone.
/// </summary>
public sealed record BillingSubscriptionSummaryDto(
    Guid SubscriptionId,
    string Status,
    int RequestedSeats,
    string Tier,
    bool CancelRequested,
    DateTimeOffset? CurrentPeriodEnd,
    int? PendingSeatCount,
    string? PendingTier);
