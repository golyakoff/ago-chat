using Ago.Platform.Abstractions;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>`23-76`: always allows - these tests exercise real concurrent writers against real
/// Postgres/Redis, never rate limiting itself, the same "stands in for tests that need a rate-limited
/// handler but are not themselves testing rate limiting" role
/// <c>Ago.Chat.Integration.Tests.FakeRateLimiter</c> already plays for that project (not shared across
/// projects, per this repository's own "test projects do not reference each other" convention).</summary>
public sealed class FakeRateLimiter : IRateLimiter
{
    public Task<RateLimitDecision> CheckAsync(RateLimitKey key, RateLimitRule rule, CancellationToken cancellationToken) =>
        Task.FromResult(new RateLimitDecision(true, TimeSpan.Zero));
}
