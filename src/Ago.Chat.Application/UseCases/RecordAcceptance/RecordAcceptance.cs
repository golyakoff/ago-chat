using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RecordAcceptance;

/// <summary>
/// `24-01`: record that <paramref name="SubjectId"/> (of kind <paramref name="SubjectKind"/>) accepted
/// version <paramref name="DocumentVersion"/> of document <paramref name="DocumentKey"/>, right now.
/// <paramref name="ClientIp"/>/<paramref name="UserAgent"/> are the caller's own request context - see
/// <see cref="AcceptanceRecord"/>'s own remarks for why exactly these two and nothing more.
///
/// <para>Deliberately carries no <c>SiteId</c>/tenant-scoping parameter and no permission check in its
/// handler: recording your own acceptance is not a privileged act gated on a permission the way writing
/// a conversation note is - it is closer to "the caller asserts a fact about themselves," the same
/// self-service shape a login or a token refresh already has. Which caller may invoke this at all, and
/// under what authentication, is `24-03`/`24-04`/`24-05`'s own concern once they build the endpoints
/// that call it - this item's Scope is the record, not who may write one.</para>
/// </summary>
public sealed record RecordAcceptance(
    AcceptanceSubjectKind SubjectKind,
    Guid SubjectId,
    string DocumentKey,
    string DocumentVersion,
    string? ClientIp = null,
    string? UserAgent = null);
