using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetDownloadUsageForSite;

/// <summary>`25-83`: the tenant's own read of its own account's download usage - the missing half
/// the console banner needs, the identical "tenant reads its own site" shape
/// <see cref="Ago.Chat.Application.UseCases.GetSuspensionStatusForSite.GetSuspensionStatusForSite"/>
/// already establishes for suspension.</summary>
public sealed record GetDownloadUsageForSite(OperatorId RequestedBy, SiteId SiteId);

/// <param name="BytesOut">This tenant's own current-month egress - `IAttachmentEgressReadStore`'s
/// own maintained aggregate, the same proxy figure `GetAttachmentDownloadUrlHandler` enforces
/// against (this item's own report states the undercounting caveat in full).</param>
/// <param name="SoftThresholdBytes">This tenant's own tier's soft (warn) threshold.</param>
/// <param name="HardThresholdBytes">This tenant's own tier's hard (block) threshold.</param>
/// <param name="IsSoftCrossed"><see langword="true"/> once <paramref name="BytesOut"/> reaches
/// <paramref name="SoftThresholdBytes"/> - the console banner's own trigger.</param>
/// <param name="IsHardCrossed"><see langword="true"/> once <paramref name="BytesOut"/> reaches
/// <paramref name="HardThresholdBytes"/> and <paramref name="IsExempt"/> is <see langword="false"/> -
/// downloads are actually refused right now.</param>
/// <param name="IsExempt">Whether the platform owner has granted this tenant's own free, indefinite
/// bypass of the hard threshold (<see cref="Site.DownloadBlockExempt"/>). A site can read
/// <c>BytesOut &gt;= HardThresholdBytes</c> as true while <see cref="IsHardCrossed"/> is false -
/// exempt and over the raw number, but not actually blocked.</param>
/// <param name="BillingMode">`25-84`: <see cref="Domain.DownloadOverageBillingMode"/>'s own member
/// name - what the console needs to tell "blocked, and there is a button that fixes it" apart from
/// "blocked, and paying is not how this tenant's account works". The platform owner sets it; the tenant
/// only ever reads it (`docs/backlog/25-84-*.md`'s own Out of scope).</param>
/// <param name="OutstandingOverageBytes">Bytes past <paramref name="HardThresholdBytes"/> that no
/// succeeded charge covers yet - zero for a tenant who is not over, or whose overage is fully
/// settled.</param>
/// <param name="OutstandingOverageRub">What <paramref name="OutstandingOverageBytes"/> costs at the
/// currently-published per-gigabyte price - <see langword="null"/> exactly when nothing has ever been
/// published for that key, which is the honest "not for sale in this deployment" state `25-43`'s own
/// second decision names. Deliberately a server-computed figure rather than a price the console
/// multiplies for itself - `25-23`'s own "a real, sourced figure on the wire, never invented
/// client-side" discipline, which is what keeps the number a tenant is asked to pay identical to the
/// number the charge is actually computed from.</param>
/// <param name="OverageSettledRub">What this month's overage has already cost, across both paths -
/// what <paramref name="AutoBillCapRub"/> is measured against together with the outstanding amount.</param>
/// <param name="AutoBillCapRub">This tier's own auto-bill ceiling, or <see langword="null"/> for
/// uncapped - see <see cref="Ago.Chat.Application.Abstractions.DownloadThresholds.AutoBillCapRub"/>.</param>
/// <param name="IsAtAutoBillCap">Whether this month's overage has reached that ceiling, so downloads are
/// blocked despite the tenant paying - the one blocked state a purchase cannot lift, which is why the
/// console must be able to tell it apart rather than offering a button that would refuse.</param>
public sealed record DownloadUsageStatus(
    long BytesOut, long SoftThresholdBytes, long HardThresholdBytes, bool IsSoftCrossed, bool IsHardCrossed, bool IsExempt,
    string BillingMode, long OutstandingOverageBytes, decimal? OutstandingOverageRub, decimal OverageSettledRub,
    decimal? AutoBillCapRub, bool IsAtAutoBillCap);
