using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RecordVisitorConsent;

/// <summary>
/// `24-05`: the visitor's own act of accepting one purpose's consent document, right now -
/// <see cref="RequestedBy"/> is the visitor, resolved from the signed visitor token, the identical
/// shape <see cref="Application.UseCases.RecordVisitorContactDetail.RecordVisitorContactDetailAsVisitor"/>
/// already uses for itself. <see cref="Purpose"/> arrives as a raw string ("Contact"/"Marketing"),
/// validated by the handler, not the HTTP endpoint - the same "handler validates, endpoint does not"
/// split every other raw-string field in this codebase already follows.
///
/// <para><b>No <c>DocumentKey</c>/<c>DocumentVersion</c> parameter - deliberately, unlike
/// <see cref="RecordAcceptance.RecordAcceptance"/> itself.</b> This command is the one real caller that
/// sits in front of that lower-level command: <c>RecordVisitorConsentHandler</c> derives the key from
/// the conversation's own site and this purpose, and reads the *current* published version itself,
/// rather than trusting a caller to have already resolved either - the same "the caller never gets to
/// name what it is accepting" reasoning <c>SiteConsentDocumentKey</c>'s own remarks give for the
/// publish side.</para>
/// </summary>
public sealed record RecordVisitorConsent(
    ConversationId ConversationId, VisitorId RequestedBy, string Purpose, string? ClientIp = null, string? UserAgent = null);

public sealed record RecordedVisitorConsent(Guid Id, string DocumentKey, string DocumentVersion, DateTimeOffset AcceptedAt);
