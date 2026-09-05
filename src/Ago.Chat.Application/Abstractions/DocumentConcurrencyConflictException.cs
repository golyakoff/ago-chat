using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `24-02`: <see cref="IDocumentRepository.SaveAsync"/>'s own technology-agnostic signal that a
/// <see cref="Document"/> row changed underneath it before this save committed - the identical
/// "translated at the port boundary so Application never sees EF's own exception type" shape
/// <see cref="ConversationConcurrencyConflictException"/> already established for `6-08`. A handler
/// that wants to retry (`PublishDocumentVersionHandler`) catches this, never
/// <c>Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException</c>.
/// </summary>
public sealed class DocumentConcurrencyConflictException(DocumentId documentId)
    : Exception($"Document {documentId.Value} was modified concurrently before it could be saved.")
{
    public DocumentId DocumentId { get; } = documentId;
}
