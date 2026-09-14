using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetSiteAttachmentStorageSummary;

public sealed record GetSiteAttachmentStorageSummary(SiteId SiteId, OperatorId RequestedBy);

/// <summary><see cref="UsedBytes"/> comes from <c>IAttachmentBudgetReadStore</c>, a bare read of the
/// exact column <c>ISiteAttachmentStorageBudget</c> (`23-76`) already maintains - see that read
/// port's own remarks for why "the number agrees with what enforcement believes" is true by
/// construction here rather than asserted. <see cref="TotalBytes"/> is
/// <c>SiteAttachmentQuotaPolicy.ComputeBudgetBytes</c>, the identical pure function
/// <c>CreateAttachmentHandler</c> calls to decide whether to *reserve* a new upload - the same
/// function, not a second reading of the tier grid, so a tier or pricing change moves both numbers
/// together by construction.</summary>
public sealed record SiteAttachmentStorageSummary(long UsedBytes, long TotalBytes);
