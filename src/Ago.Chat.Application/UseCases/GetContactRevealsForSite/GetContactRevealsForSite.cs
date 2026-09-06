using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetContactRevealsForSite;

/// <summary>
/// `23-11`'s own Done-when: "the tenant can read the reveal record, and the screen says what it is
/// for and what it is not." `GET /api/v1/sites/{siteId}/contact-reveals` - the tenant-facing sibling of
/// `GetAccessRecordsForSite`, over a different, purpose-built table (<c>IContactRevealRepository</c>'s
/// own remarks on why this is not folded into `access_records`). <paramref name="Before"/>
/// <see langword="null"/> means the first page.
/// </summary>
public sealed record GetContactRevealsForSite(SiteId SiteId, OperatorId RequestedBy, Guid? Before, int? Limit);
