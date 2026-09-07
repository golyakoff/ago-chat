namespace Ago.Chat.Application.UseCases.GetSiteConsentDocuments;

/// <summary>
/// `23-37`'s own wire-independent read shape - see <see cref="GetSiteConsentDocumentsHandler"/>'s
/// remarks for why <see cref="ContactConsentRequired"/> sits beside <see cref="Contact"/> rather than
/// inside it: it is a fact about the site's <c>WidgetConfig</c>, not about the document.
/// </summary>
public sealed record SiteConsentDocumentsOverview(
    SiteConsentDocumentSummary Contact, bool ContactConsentRequired, SiteConsentDocumentSummary Marketing);

/// <summary>One of a site's two consent-purpose documents - <see cref="DocumentKey"/> is the exact
/// string <see cref="Domain.SiteConsentDocumentKey.For"/> derived, handed back so a caller (the console)
/// never has to recompute or hardcode that format itself, e.g. to reach the public, unauthenticated
/// read surface (`GET /api/v1/documents/{documentKey}`) "as a visitor would".</summary>
public sealed record SiteConsentDocumentSummary(
    string Purpose, string DocumentKey, IReadOnlyList<PublishedVersionSummary> Versions);

/// <summary>One published version, newest-first ordering owned by the caller
/// (<see cref="Abstractions.IDocumentRepository.ListVersionsAsync"/>) - no document body here, this is
/// a list screen's own row, not the "read a specific version" surface `24-02` already built.</summary>
public sealed record PublishedVersionSummary(string Version, int Sequence, string Title, DateTimeOffset PublishedAt);
