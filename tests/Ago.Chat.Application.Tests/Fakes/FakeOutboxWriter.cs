using Ago.Platform.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Records what was staged, without any of EfOutboxWriter's DbContext/transaction
/// behaviour - that guarantee is proven against real Postgres in Ago.Chat.Integration.Tests
/// (testing.md: never mock the database for a guarantee the schema itself provides).</summary>
public sealed class FakeOutboxWriter : IOutboxWriter
{
    private readonly List<EventEnvelope> _enqueued = [];

    public IReadOnlyList<EventEnvelope> Enqueued => _enqueued;

    public void Enqueue(EventEnvelope envelope) => _enqueued.Add(envelope);
}
