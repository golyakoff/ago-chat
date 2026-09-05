using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Records every call instead of touching Postgres/object storage - good enough to prove
/// <c>ExportConversationHandler</c>/<c>ExportVisitorHandler</c> resolved the right scope and passed it
/// through, without needing a real archive (`PersonExportIntegrationTests` proves the archive's own
/// contents against a real Postgres).</summary>
public sealed class FakePersonExportArchiveWriter : IPersonExportArchiveWriter
{
    public List<PersonExportCall> Calls { get; } = [];

    public Task<Stream> WriteAsync(
        SiteId siteId, VisitorId visitorId, IReadOnlyList<ConversationId> conversationIds, string scope,
        DateTimeOffset exportedAt, CancellationToken cancellationToken)
    {
        Calls.Add(new PersonExportCall(siteId, visitorId, conversationIds, scope, exportedAt));
        Stream stream = new MemoryStream([1, 2, 3]);
        return Task.FromResult(stream);
    }
}

public sealed record PersonExportCall(
    SiteId SiteId, VisitorId VisitorId, IReadOnlyList<ConversationId> ConversationIds, string Scope, DateTimeOffset ExportedAt);
