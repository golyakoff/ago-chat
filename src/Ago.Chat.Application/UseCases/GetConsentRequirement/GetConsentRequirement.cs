using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetConsentRequirement;

/// <summary>
/// `24-05`: "does this conversation's own site require a recorded consent before it will accept a
/// contact detail, and if so what does the tenant's own document currently say, and has this visitor
/// already accepted it" - the one read the widget needs before it decides whether to show a consent
/// control alongside `23-09`'s name-and-phone form at all.
///
/// <para>Visitor-only, conversation-scoped - the identical shape
/// <c>RecordVisitorContactDetailAsVisitor</c> already uses for itself: <see cref="RequestedBy"/> comes
/// from the signed visitor token, never from a parameter a caller could substitute another visitor's
/// id into, and <see cref="ConversationId"/> is both "which conversation" and (through it) "which
/// site" - this query carries no <see cref="SiteId"/> of its own.</para>
/// </summary>
public sealed record GetConsentRequirement(ConversationId ConversationId, VisitorId RequestedBy);

/// <summary>One purpose's own current state - <see langword="null"/> fields mean "required, but the
/// tenant has not published anything under this purpose's key yet" (never an error: the identical
/// "coming soon" reading `RequiredDocumentSummary`'s own remarks already give a required-but-unpublished
/// key), and <see cref="AlreadyAccepted"/> is <see langword="true"/> only once this specific visitor's
/// own acceptance record exists for this purpose's key, any version (`GetConsentRequirementHandler`'s
/// own remarks on why a version match is not required).</summary>
public sealed record ConsentDocumentSummary(string DocumentKey, string? Version, string? Title, string? Body, DateTimeOffset? PublishedAt);

/// <summary><see cref="ContactRequired"/> is <see cref="WidgetConfig.RequireContactConsent"/>, read
/// straight through - when it is <see langword="false"/>, both document summaries are
/// <see langword="null"/> and both "already accepted" flags are <see langword="false"/>: there is
/// nothing to show, the identical "no control at all" answer `24-03`'s own registration screen gives
/// when nothing is required. <see cref="Marketing"/> is never required - it rides along purely so the
/// same one read tells the widget whether a marketing checkbox has anything to show at all.</summary>
public sealed record ConsentRequirement(
    bool ContactRequired, ConsentDocumentSummary? Contact, bool ContactAlreadyAccepted,
    ConsentDocumentSummary? Marketing, bool MarketingAlreadyAccepted);
