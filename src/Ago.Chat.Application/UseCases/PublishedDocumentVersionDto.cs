namespace Ago.Chat.Application.UseCases;

/// <summary>
/// `24-02`: what both `PublishDocumentVersion` and `GetDocumentVersion` hand back - a plain,
/// serializable shape (this is also exactly what <c>ICache</c> stores, so it doubles as the cached
/// value), never <see cref="Ago.Chat.Domain.PublishedDocumentVersion"/> itself, the same
/// Domain/Application boundary <c>SiteConfigDto</c>'s own remarks already draw for <c>Site</c>.
/// </summary>
public sealed record PublishedDocumentVersionDto(
    string DocumentKey, string Version, int Sequence, string Title, string Body, DateTimeOffset PublishedAt);
