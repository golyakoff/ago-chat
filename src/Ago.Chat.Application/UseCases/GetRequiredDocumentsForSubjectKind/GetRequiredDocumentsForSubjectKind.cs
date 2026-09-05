using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetRequiredDocumentsForSubjectKind;

/// <summary>
/// `24-03`: "which documents does a subject of <paramref name="SubjectKind"/> have to accept, and what
/// do they currently say" - the one read a pre-account screen (a registration form, deliberately, since
/// `24-02`'s own Scope: "somebody who has not yet accepted anything has no account to read it from")
/// needs in order to show a link to the current agreement without the screen itself hardcoding which
/// document key that is. Deliberately unauthenticated - see this feature's own host endpoint
/// (`Ago.Chat.Api.Documents.DocumentEndpoints`) for the reasoning, the same as `24-02`'s published
/// surface right beside it.
/// </summary>
public sealed record GetRequiredDocumentsForSubjectKind(AcceptanceSubjectKind SubjectKind);

/// <summary>One required document, joined against `24-02`'s own published surface.
/// <paramref name="Version"/>/<paramref name="Title"/>/<paramref name="PublishedAt"/> are
/// <see langword="null"/> when the key is required but nothing has been published under it yet -
/// `RegisterSiteHandler`'s own `Site.AgreementUnavailable` case, surfaced here as data rather than a
/// failure: a caller reading this list gets an honest "required, not yet readable" entry instead of
/// this query failing outright for one misconfigured key among possibly several.</summary>
public sealed record RequiredDocumentSummary(string DocumentKey, string? Version, string? Title, DateTimeOffset? PublishedAt);
