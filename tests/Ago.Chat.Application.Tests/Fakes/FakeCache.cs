using Ago.Platform.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Standing in for <c>Ago.Platform.Caching.Redis.RedisCache</c> - real cache-aside
/// semantics (a value already present is returned without calling the factory again; a factory that
/// self-populates via <see cref="SetAsync{T}"/> is left alone, matching
/// <see cref="ICache.GetOrCreateAsync{T}"/>'s own doc comment) without needing Redis for an
/// Application-level test.</summary>
public sealed class FakeCache : ICache
{
    private readonly Dictionary<string, object?> _store = [];

    public int FactoryCalls { get; private set; }

    public Task<T?> GetAsync<T>(CacheKey key, CancellationToken cancellationToken) where T : class =>
        Task.FromResult(_store.TryGetValue(key.Value, out var value) ? (T?)value : default);

    public Task SetAsync<T>(CacheKey key, T value, CacheEntryOptions options, CancellationToken cancellationToken) where T : class
    {
        _store[key.Value] = value;
        return Task.CompletedTask;
    }

    public async Task<T> GetOrCreateAsync<T>(
        CacheKey key, Func<CancellationToken, Task<T>> factory, CacheEntryOptions options, CancellationToken cancellationToken)
        where T : class
    {
        if (_store.TryGetValue(key.Value, out var cached))
        {
            return (T)cached!;
        }

        FactoryCalls++;
        var value = await factory(cancellationToken);
        if (!_store.ContainsKey(key.Value))
        {
            _store[key.Value] = value;
        }

        return value;
    }

    public Task RemoveAsync(CacheKey key, CancellationToken cancellationToken)
    {
        _store.Remove(key.Value);
        return Task.CompletedTask;
    }
}
