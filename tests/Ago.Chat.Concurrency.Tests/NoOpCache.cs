using Ago.Platform.Abstractions;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>Never caches - every read is a miss, every factory runs. Stands in for tests that need a
/// working <see cref="ICache"/> (`13-06`'s <c>MessageBatchWriter</c> now resolves a site's tier
/// through one to stamp <c>RetentionClass</c>) but are not themselves testing caching behaviour - the
/// same role <c>NodeDeathReconnectTests</c>'s own nested `NoOpCache` already plays for that one test
/// class, pulled out to a shared file since another unrelated test class in this project now needs
/// the identical stand-in.</summary>
public sealed class NoOpCache : ICache
{
    public Task<T?> GetAsync<T>(CacheKey key, CancellationToken cancellationToken) where T : class =>
        Task.FromResult<T?>(default);

    public Task SetAsync<T>(CacheKey key, T value, CacheEntryOptions options, CancellationToken cancellationToken) where T : class =>
        Task.CompletedTask;

    public Task<T> GetOrCreateAsync<T>(
        CacheKey key, Func<CancellationToken, Task<T>> factory, CacheEntryOptions options, CancellationToken cancellationToken) where T : class =>
        factory(cancellationToken);

    public Task RemoveAsync(CacheKey key, CancellationToken cancellationToken) => Task.CompletedTask;
}
