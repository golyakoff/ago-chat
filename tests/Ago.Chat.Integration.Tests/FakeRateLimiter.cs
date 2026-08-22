using Ago.Platform.Abstractions;

namespace Ago.Chat.Integration.Tests;

/// <summary>Always allows - stands in for tests that need a rate-limited handler but are not
/// themselves testing rate limiting (`RateLimitingTests` is the one that is).</summary>
public sealed class FakeRateLimiter : IRateLimiter
{
    public Task<RateLimitDecision> CheckAsync(RateLimitKey key, RateLimitRule rule, CancellationToken cancellationToken) =>
        Task.FromResult(new RateLimitDecision(true, TimeSpan.Zero));
}
