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
/// construction.
///
/// <para><b>`25-23`: five fields added, all additive - `api-design.md`'s "add within a version, never
/// remove or rename" (the four original fields keep their original names and positions).</b>
/// <see cref="TierDisplayName"/>, the Administrator-seat pair, the purchased-extra-Administrators fact,
/// and the sourced seat-pricing/range figures are exactly what `ago-console`'s billing page needs to
/// stop rendering a raw enum and a stale hand-typed seat-count range - `docs/backlog/25-23-*.md`'s own
/// Scope.</para>
///
/// <para><b><see cref="AdminsUsed"/> is <see cref="Ago.Chat.Application.Abstractions.IOperatorRoleRepository.GetNonRemovedHolderIdsAsync"/>'s
/// own count - not <see cref="Ago.Chat.Application.Abstractions.IOperatorRoleRepository.CountNonRemovedHoldersAsync"/>.</b>
/// That sibling method takes a `FOR UPDATE` lock on the site row and requires an ambient transaction -
/// the right tool for a count-then-act write decision (`ChangeOperatorRoleHandler`'s own promotion
/// guard), the wrong one for a plain display read that would otherwise serialize every concurrent
/// billing-page load behind a lock nothing here needs to hold (this handler's own remarks: "no lock, no
/// transaction - nothing here for a lock to protect"). <see cref="GetNonRemovedHolderIdsAsync"/> runs
/// the identical predicate with no lock of its own, the same trade <see cref="SeatsUsed"/> already makes
/// against <see cref="Ago.Chat.Application.Abstractions.IOperatorRepository.CountHeldSeatsAsync"/>.</para>
///
/// <para><b><see cref="ExtraAdministratorsPurchased"/> is read straight off the base subscription this
/// handler already loads</b> (<c>latest</c>), never derived by subtracting
/// <see cref="Ago.Chat.Domain.SubscriptionTierBands.ResolveAdminLimit"/> from <see cref="AdminLimit"/> -
/// the entity's own field is the one fact `25-41` actually persists; re-deriving it here would work
/// today but silently drift the moment <see cref="Ago.Chat.Domain.Site.ActivateSubscription"/>'s own
/// formula ever changes. Zero for a site that has never purchased an extra Administrator (including one
/// that has never checked out at all).</para>
///
/// <para><b>Tier-name mapping decision, made here rather than left to `ago-console`: server-side.</b>
/// <see cref="Tier"/> stays the raw enum value on the wire, unchanged, for whatever already reads it
/// literally; <see cref="TierDisplayName"/> is the one new field that resolves it to the business name
/// `ago-business 0012` actually uses ("Solo"/"Business"). Mapping here, not in the console, because the
/// mapping is a two-branch, tier=="free" split - the identical predicate
/// <see cref="Ago.Chat.Domain.SubscriptionTierBands.ResolveAdminLimit"/> already uses one line above it
/// in the same file - so centralising it keeps every future reader of this DTO (today just one console
/// page, plausibly more later) from re-typing the same two names and risking one of them drifting from
/// the grid's own wording. The alternative (ship the raw tier string, let each client branch on it) was
/// rejected for the same reason `SubscriptionTierBands.ResolveAdminLimit` itself is not duplicated at
/// every call site: one bare `tier == "free"` check belongs in one place.</para>
///
/// <para><b>Seat-range/price copy: sourced from <see cref="Ago.Chat.Domain.SubscriptionTierBands"/> and
/// <see cref="Ago.Chat.Application.Abstractions.IPriceCatalogRepository"/>, the identical two sources
/// `GetPricingForOwnerHandler` already reads for the owner's own price-list screen (`25-20`'s "sourced,
/// not retyped" discipline, applied a second time rather than left for the console to hand-type a
/// second, differently-worded copy of the same numbers).</b> <see cref="BillingSeatPricingDto"/> is a
/// distinct type from <see cref="Ago.Chat.Contracts.OwnerSeatPricingDto"/>, not a reuse of it: that type
/// carries a `Tiers` list and a legacy `PricePerSeatRub` wire-compatibility field built for the owner's
/// administrative screen (`OwnerSeatPricingDto`'s own remarks); a tenant's billing page needs only the
/// numbers that describe its own purchasable range and current cost, and a "why does my billing status
/// carry an owner-shaped `Tiers` array" question is worse than the few duplicated fields.</para>
///
/// <para><see cref="AdminExtraPriceRub"/> is <see langword="null"/> exactly when
/// <see cref="Ago.Chat.Domain.SubscriptionTierBands.AdminExtraPriceKey"/> has never had a version
/// published - `25-43`'s own second decision ("a key with no published version is the ordinary
/// 'built, not yet for sale' state"), the identical honest-null <see cref="Ago.Chat.Contracts.OwnerPricedResourceDto.CurrentAmountRub"/>
/// already uses for the owner's screen. Unlike the two seat-pricing keys below (thrown on, as
/// <c>GetPricingForOwnerHandler</c> already does - the migration seed that ships this mechanism
/// publishes both on day one, so their absence is a deployment defect worth a loud failure, never a
/// fabricated zero), a never-purchased Administrator slot is the ordinary state for every site that has
/// not bought one yet, so this field being <see langword="null"/> is not itself surprising - the
/// console decides how to render "not priced yet" versus "priced at ₽N", this DTO only states the
/// fact.</para>
/// </summary>
public sealed record BillingStatusDto(
    string Tier,
    int SeatLimit,
    int SeatsUsed,
    BillingSubscriptionSummaryDto? LatestSubscription,
    string TierDisplayName,
    int AdminLimit,
    int AdminsUsed,
    int ExtraAdministratorsPurchased,
    BillingSeatPricingDto SeatPricing,
    decimal? AdminExtraPriceRub);

/// <summary>
/// `25-23`: the Operator seat-purchase formula's own currently-effective numbers, read the identical
/// way <see cref="Ago.Chat.Contracts.OwnerSeatPricingDto"/> already is (see <see cref="BillingStatusDto"/>'s
/// own remarks for why this is a separate, narrower type rather than a reuse of that one). Every field
/// here is the tenant-facing replacement for `ago-console`'s stale, hand-typed "От 2 до 100 мест" copy -
/// `25-23`'s own Scope names this drift by name.
/// </summary>
/// <param name="MinSeats"><see cref="Ago.Chat.Domain.SubscriptionTierBands.MinSeats"/> - the fewest
/// seats a Business purchase can name.</param>
/// <param name="MaxSeats"><see cref="Ago.Chat.Domain.SubscriptionTierBands.MaxSeats"/> - the most seats
/// a self-serve purchase can name today; nothing above this is sold without a conversation.</param>
/// <param name="BaseSeats"><see cref="Ago.Chat.Domain.SubscriptionTierBands.BaseSeats"/> - how many
/// seats <paramref name="BaseSeatPriceRub"/> alone covers before <paramref name="PricePerExtraSeatRub"/>
/// starts applying.</param>
/// <param name="FreeSeatsIncluded"><see cref="Ago.Chat.Domain.SubscriptionTierBands.FreeSeatsIncluded"/> -
/// how many seats every site starts on before any purchase, at no charge.</param>
/// <param name="BaseSeatPriceRub">The currently-effective published price for
/// <see cref="Ago.Chat.Domain.SubscriptionTierBands.BaseSeatPriceKey"/>.</param>
/// <param name="PricePerExtraSeatRub">The currently-effective published price for
/// <see cref="Ago.Chat.Domain.SubscriptionTierBands.ExtraSeatPriceKey"/> - the marginal charge for every
/// seat past <paramref name="BaseSeats"/>.</param>
/// <param name="BillingPeriodDays"><see cref="Ago.Chat.Domain.BillingSubscription.PeriodLength"/>'s own
/// `TotalDays` - how often this pricing is charged.</param>
public sealed record BillingSeatPricingDto(
    int MinSeats,
    int MaxSeats,
    int BaseSeats,
    int FreeSeatsIncluded,
    decimal BaseSeatPriceRub,
    decimal PricePerExtraSeatRub,
    double BillingPeriodDays);

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
