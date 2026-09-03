using Ago.Platform.Abstractions;

namespace Ago.Chat.Integration.Tests;

/// <summary>Always allows - stands in for tests that need a rate-limited handler but are not
/// themselves testing rate limiting (`RateLimitingTests` is the one that is).</summary>
public sealed class FakeRateLimiter : IRateLimiter
{
    public Task<RateLimitDecision> CheckAsync(RateLimitKey key, RateLimitRule rule, CancellationToken cancellationToken) =>
        Task.FromResult(new RateLimitDecision(true, TimeSpan.Zero));
}

/// <summary>Always denies, with a fixed retry-after - the counterpart to <see cref="FakeRateLimiter"/>
/// for asserting the denied path (`ago-root#347`'s `DemoEndpointRateLimitTests`, the same shape
/// `Ago.Chat.Application.Tests.Fakes.RateLimitedFakeRateLimiter` already gives that project; not
/// shared across the two, per this repository's own "test projects do not reference each other"
/// convention, e.g. `DemoTenantLifecycleTests`'s own remarks).</summary>
public sealed class RateLimitedFakeRateLimiter(TimeSpan retryAfter) : IRateLimiter
{
    public Task<RateLimitDecision> CheckAsync(RateLimitKey key, RateLimitRule rule, CancellationToken cancellationToken) =>
        Task.FromResult(new RateLimitDecision(false, retryAfter));
}
