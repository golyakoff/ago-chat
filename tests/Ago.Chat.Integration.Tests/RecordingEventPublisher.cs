using Ago.Platform.Abstractions;

namespace Ago.Chat.Integration.Tests;

/// <summary>Records every envelope it is asked to publish - handed to
/// <see cref="Ago.Platform.Caching.Redis.CacheInvalidationPublisher"/>'s own constructor so
/// <c>SiteErasureIntegrationTests</c> can assert exactly which cache keys a site erasure invalidated,
/// without a fourth Testcontainer (a real RabbitMQ) to carry the message - see
/// <see cref="ErasureFixture"/>'s own remarks on why that broker hop is out of scope for this
/// item's proof.</summary>
public sealed class RecordingEventPublisher : IEventPublisher
{
    private readonly List<EventEnvelope> _published = [];

    public IReadOnlyList<EventEnvelope> Published => _published;

    public Task PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        _published.Add(envelope);
        return Task.CompletedTask;
    }
}
