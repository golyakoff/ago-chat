using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetSiteConsentAcceptances;

/// <summary>
/// `23-37`: "which version did this person accept, and when" - the read a tenant asked
/// `PublishDocumentVersion.PublishSiteConsentDocumentVersion`'s own remarks name as the item's Goal
/// ("a tenant asked 'prove they agreed' should not need us"). <paramref name="Purpose"/> arrives as a
/// raw string and is validated inside the handler - the identical split
/// <see cref="PublishDocumentVersion.PublishSiteConsentDocumentVersion"/> already establishes for
/// itself, for the same reason (the HTTP endpoint should not own enum-parsing rules).
///
/// <para>No <c>documentKey</c> here either - <see cref="SiteConsentDocumentKey.For"/> derives it from
/// <see cref="SiteId"/>/<see cref="Purpose"/> inside the handler, after the permission check, so a
/// caller can never point this read at a document key outside their own site's own two.</para>
/// </summary>
public sealed record GetSiteConsentAcceptances(SiteId SiteId, string Purpose, OperatorId RequestedBy);
