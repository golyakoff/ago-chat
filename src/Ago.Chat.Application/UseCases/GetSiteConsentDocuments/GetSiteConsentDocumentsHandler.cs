using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetSiteConsentDocuments;

/// <summary>
/// `23-37`. The permission check is the entire tenant-isolation story for this read, the same shape
/// <see cref="Ago.Chat.Application.UseCases.PublishDocumentVersion.PublishDocumentVersionHandler.HandleAsSiteConsentAsync"/>
/// already established for the write half of this same surface: it is checked against <c>query.SiteId</c>
/// (the site named by the caller), never against anything read off the resulting document, because there
/// is nothing on a <see cref="Domain.PublishedDocumentVersion"/> row that names a tenant to compare
/// against - the row's own key already encodes it (<see cref="SiteConsentDocumentKey"/>'s own remarks).
/// A caller whose token does not hold <see cref="Permission.SiteConfigure"/> on the named site is
/// refused before either of the two per-purpose reads below ever runs.
/// </summary>
public sealed class GetSiteConsentDocumentsHandler(
    ISiteRepository sites, IPermissionChecker permissions, IDocumentRepository documents)
{
    public async Task<Result<SiteConsentDocumentsOverview>> HandleAsync(
        GetSiteConsentDocuments query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to view this site's consent documents.");
        }

        var site = await sites.GetByIdAsync(query.SiteId, cancellationToken);
        if (site is null)
        {
            return ConversationErrors.SiteNotFound(query.SiteId.Value);
        }

        var contact = await BuildSummaryAsync(query.SiteId, VisitorConsentPurpose.Contact, cancellationToken);
        var marketing = await BuildSummaryAsync(query.SiteId, VisitorConsentPurpose.Marketing, cancellationToken);

        return new SiteConsentDocumentsOverview(contact, site.WidgetConfig.RequireContactConsent, marketing);
    }

    private async Task<SiteConsentDocumentSummary> BuildSummaryAsync(
        SiteId siteId, VisitorConsentPurpose purpose, CancellationToken cancellationToken)
    {
        var documentKey = SiteConsentDocumentKey.For(siteId, purpose);
        var versions = await documents.ListVersionsAsync(documentKey, cancellationToken);
        return new SiteConsentDocumentSummary(
            purpose.ToString(),
            documentKey,
            versions.Select(v => new PublishedVersionSummary(v.Version, v.Sequence, v.Title, v.PublishedAt)).ToList());
    }
}
