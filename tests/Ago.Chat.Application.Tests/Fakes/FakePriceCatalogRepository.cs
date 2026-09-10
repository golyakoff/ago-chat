using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`25-43`. An in-memory stand-in for <see cref="IPriceCatalogRepository"/> - the identical
/// "no concurrency simulation here, that race is proven against a real Postgres container" split
/// <see cref="FakeDocumentRepository"/>'s own remarks describe for `24-02`
/// (<c>PriceCatalogRepositoryTests</c>, against a real container, is where the race is actually
/// proven for this mechanism).</summary>
public sealed class FakePriceCatalogRepository : IPriceCatalogRepository
{
    private readonly Dictionary<string, PricedResource> _resources = [];

    public Task<PricedResource?> GetByKeyAsync(PriceKey key, CancellationToken cancellationToken) =>
        Task.FromResult(_resources.GetValueOrDefault(key.Value));

    public Task SaveAsync(PricedResource resource, CancellationToken cancellationToken)
    {
        _resources[resource.Key.Value] = resource;
        return Task.CompletedTask;
    }

    public Task<PublishedPriceVersion?> FindCurrentAsync(PriceKey key, CancellationToken cancellationToken) =>
        Task.FromResult(_resources.GetValueOrDefault(key.Value)?.Current);

    public Task<PublishedPriceVersion?> FindVersionAsync(PriceKey key, int sequence, CancellationToken cancellationToken) =>
        Task.FromResult(
            _resources.TryGetValue(key.Value, out var resource)
                ? resource.Versions.FirstOrDefault(v => v.Sequence == sequence)
                : null);

    /// <summary>Test convenience, not part of the port: publishes one version directly (bypassing
    /// <c>PublishPriceVersionHandler</c> entirely, the same "seed the fake straight from the aggregate"
    /// shortcut a handler test takes when it only needs a price to already exist, not to exercise the
    /// publish flow itself). Returns the minted <see cref="PublishedPriceVersion.Sequence"/> so a test
    /// can seed <see cref="BillingSubscription.BaseSeatPriceVersion"/>/<see cref="BillingSubscription.ExtraSeatPriceVersion"/>
    /// with a value this same fake will actually resolve through <see cref="FindVersionAsync"/>.</summary>
    public int SeedVersion(PriceKey key, decimal amountRub, DateTimeOffset publishedAt)
    {
        var resource = _resources.GetValueOrDefault(key.Value) ?? PricedResource.Create(new PricedResourceId(Guid.NewGuid()), key);
        var version = resource.Publish(new PublishedPriceVersionId(Guid.NewGuid()), amountRub, publishedAt);
        _resources[key.Value] = resource;
        return version.Sequence;
    }
}
