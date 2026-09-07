using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetSiteConsentDocuments;

/// <summary>
/// `23-37`: the read behind the console's own "Документы" screen - both of a site's own visitor-facing
/// consent documents (<see cref="VisitorConsentPurpose.Contact"/>/<see cref="VisitorConsentPurpose.Marketing"/>),
/// every version each has ever had, newest first. Gated by <see cref="Application.Abstractions.IPermissionChecker"/>
/// against <see cref="Permission.SiteConfigure"/> - the identical permission
/// <c>PublishDocumentVersionHandler.HandleAsSiteConsentAsync</c> already checks for this exact site, because
/// reading what a tenant has published is the same "configure this site" act as writing it
/// (`authorization.md`'s own "site:configure gates a second, distinct thing" precedent, applied a third
/// time rather than inventing a narrower permission).
///
/// <para>No <c>documentKey</c> anywhere in this command, for the identical reason
/// <see cref="PublishDocumentVersion.PublishSiteConsentDocumentVersion"/> carries none:
/// <see cref="SiteConsentDocumentKey.For"/> derives both of the two keys this read touches from
/// <see cref="SiteId"/> alone, inside the handler - there is no request field a caller could set to make
/// this read reach a document outside their own site.</para>
/// </summary>
public sealed record GetSiteConsentDocuments(SiteId SiteId, OperatorId RequestedBy);
