using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.PublishDocumentVersion;

/// <summary>
/// `24-02`: publish a new version of <paramref name="DocumentKey"/> with the given
/// <paramref name="Title"/>/<paramref name="Body"/>, right now. The version identifier is never a
/// caller-supplied field - <see cref="Domain.PublishedDocumentVersion"/>'s own remarks explain why it
/// is minted server-side instead.
///
/// <para>Deliberately carries no permission check of its own in its handler - the entire
/// access-control story is <c>OwnerDocumentEndpoints</c>'s <c>RequirePlatformOwner</c> gate, the same
/// single-gate shape every other owner surface in this codebase already uses
/// (<c>OwnerModuleEndpoints</c>'s own remarks). This command has no <c>SiteId</c> to check a permission
/// against in the first place - a document is not tenant-scoped, it is AGO's own.</para>
/// </summary>
public sealed record PublishDocumentVersion(string DocumentKey, string Title, string Body);

/// <summary>
/// `24-05`: the tenant's own counterpart, right beside <see cref="PublishDocumentVersion"/> above -
/// same handler (<see cref="PublishDocumentVersionHandler.HandleAsSiteConsentAsync"/>), a genuinely
/// different authorization shape. Unlike the AGO-owner command above, this one carries a
/// <see cref="SiteId"/> and is gated by <see cref="Application.Abstractions.IPermissionChecker"/>
/// against <see cref="Permission.SiteConfigure"/> - the same permission `UpdateWidgetConfigHandler`
/// already checks for this exact tenant, because publishing the tenant's own consent text is a
/// widget-configuration act, not a new capability.
///
/// <para><b>No <c>DocumentKey</c> parameter at all - deliberately.</b> `SiteConsentDocumentKey.For`
/// derives it from <see cref="SiteId"/>/<see cref="Purpose"/> inside the handler, never from anything
/// this command carries - see that type's own remarks for why a caller-chosen key here would be a real
/// cross-tenant (or cross-document) write hazard, not merely an unnecessary parameter.</para>
///
/// <para><paramref name="Purpose"/> arrives as a raw string, not yet <see cref="VisitorConsentPurpose"/>
/// - the same "the handler validates it, not the HTTP endpoint" split
/// <c>RecordVisitorContactDetailAsOperator.Kind</c> already establishes for itself.</para>
/// </summary>
public sealed record PublishSiteConsentDocumentVersion(
    SiteId SiteId, OperatorId RequestedBy, string Purpose, string Title, string Body);
