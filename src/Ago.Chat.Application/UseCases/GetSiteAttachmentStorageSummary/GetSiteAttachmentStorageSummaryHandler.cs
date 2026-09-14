using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.CreateAttachment;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetSiteAttachmentStorageSummary;

/// <summary>
/// `23-80`'s own first Done-when box: "a tenant sees how much of their quota is used, and it agrees
/// with what enforcement believes." Both halves of that sentence are answered by reusing `23-76`'s own
/// pieces rather than recomputing anything - <see cref="IAttachmentBudgetReadStore"/> for the used
/// figure, <see cref="SiteAttachmentQuotaPolicy"/> (the exact static method
/// <c>CreateAttachmentHandler</c> already calls) for the ceiling - so this handler owns no arithmetic
/// of its own beyond calling both and packaging the pair.
///
/// Gated on <see cref="Permission.SiteConfigure"/>, the same choice
/// <c>ListSiteAttachmentsHandler</c>'s own remarks explain for the whole storage screen.
/// </summary>
public sealed class GetSiteAttachmentStorageSummaryHandler(
    IAttachmentBudgetReadStore budgetReads,
    ISiteRepository sites,
    IBillingSubscriptionRepository billingSubscriptions,
    IPermissionChecker permissions,
    AttachmentStorageQuotaOptions storageQuotaOptions,
    IClock clock)
{
    public async Task<Result<SiteAttachmentStorageSummary>> HandleAsync(
        GetSiteAttachmentStorageSummary query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to read this site's attachment storage.");
        }

        var site = await sites.GetByIdAsync(query.SiteId, cancellationToken);
        if (site is null)
        {
            return ConversationErrors.SiteNotFound(query.SiteId.Value);
        }

        var baseSubscription = await billingSubscriptions.GetBaseForSiteAsync(query.SiteId, cancellationToken);
        var totalBytes = SiteAttachmentQuotaPolicy.ComputeBudgetBytes(
            storageQuotaOptions, site.Tier, baseSubscription?.CreatedAt, clock.UtcNow);

        var usedBytes = await budgetReads.GetReservedBytesAsync(query.SiteId, cancellationToken);

        return new SiteAttachmentStorageSummary(usedBytes, totalBytes);
    }
}
