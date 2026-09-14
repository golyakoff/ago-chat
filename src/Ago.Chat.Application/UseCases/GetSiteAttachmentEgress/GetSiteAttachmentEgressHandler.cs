using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetSiteAttachmentEgress;

/// <summary>
/// `23-82`'s own second Done-when box: "the measured figure is written down, so the ceiling is chosen
/// against a fact" - this is where it becomes visible rather than only accumulating in a table nobody
/// reads. Gated on <see cref="Permission.SiteConfigure"/>, the identical reasoning
/// <c>ListSiteAttachmentsHandler</c>'s own remarks give: this is exposed on the tenant's own console
/// (`23-80`'s storage screen carries it alongside the quota bar) rather than on a platform-owner-only
/// surface, because `23-82`'s own backlog item never says AGO staff must be the *only* audience, and a
/// tenant is at least as likely to want to know why their bill moved. A platform-owner reading the
/// identical figure across every tenant is a different, wider report this item's own Scope does not
/// ask for and is not built here.
/// </summary>
public sealed class GetSiteAttachmentEgressHandler(
    IAttachmentEgressReadStore reads, IPermissionChecker permissions, IClock clock)
{
    public async Task<Result<SiteAttachmentEgress>> HandleAsync(
        GetSiteAttachmentEgress query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to read this site's attachment egress.");
        }

        var periodMonth = query.PeriodMonth ?? FirstOfMonth(clock.UtcNow);

        var egress = await reads.GetForSiteAsync(query.SiteId, periodMonth, cancellationToken);
        return egress;
    }

    private static DateOnly FirstOfMonth(DateTimeOffset now) => new(now.Year, now.Month, 1);
}
