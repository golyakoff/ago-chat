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
public sealed record DownloadUsageStatus(
    long BytesOut, long SoftThresholdBytes, long HardThresholdBytes, bool IsSoftCrossed, bool IsHardCrossed, bool IsExempt);
