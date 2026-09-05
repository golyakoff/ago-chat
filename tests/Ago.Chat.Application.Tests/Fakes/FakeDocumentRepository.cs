using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`24-02`. An in-memory stand-in for <see cref="IDocumentRepository"/> - no concurrency
/// simulation (that race is proven against a real Postgres container in
/// <c>PublishedDocumentIntegrationTests</c>, the same split <c>FakeConversationRepository</c> and
/// <c>ConversationConcurrencyConflictTests</c> already draw for <c>Conversation</c>).</summary>
public sealed class FakeDocumentRepository : IDocumentRepository
{
    private readonly Dictionary<string, Document> _documents = [];

    public Task<Document?> GetByKeyAsync(string documentKey, CancellationToken cancellationToken) =>
        Task.FromResult(_documents.GetValueOrDefault(documentKey));

    public Task SaveAsync(Document document, CancellationToken cancellationToken)
    {
        _documents[document.DocumentKey] = document;
        return Task.CompletedTask;
    }

    public Task<PublishedDocumentVersion?> FindVersionAsync(string documentKey, string version, CancellationToken cancellationToken) =>
        Task.FromResult(
            _documents.TryGetValue(documentKey, out var document)
                ? document.Versions.FirstOrDefault(v => v.Version == version)
                : null);

    public Task<PublishedDocumentVersion?> FindCurrentAsync(string documentKey, CancellationToken cancellationToken) =>
        Task.FromResult(_documents.GetValueOrDefault(documentKey)?.Current);
}
