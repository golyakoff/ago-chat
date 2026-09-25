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

/// <summary>`26-108`: a real (if simplified) token bucket per key, entirely in-process - the stand-in
/// for `RedisRateLimiter` when a test needs the actual claim-once-per-window arithmetic
/// (`OperatorPresenceLostDedupTests`), not just "always allow"/"always deny". Same algorithm
/// `caching.md`'s "Rate limiting and counters" section describes: a key with no prior state starts
/// with a full bucket (so the first check for any key is always <c>Allowed</c>), each check consumes
/// one token if available, and tokens refill continuously at <see cref="RateLimitRule.RefillPerSecond"/>
/// up to <see cref="RateLimitRule.Capacity"/> - so with <c>Capacity: 1</c>, exactly one token refills
/// after <c>1 / RefillPerSecond</c> seconds, which is what lets a test cross the suppression window by
/// simply waiting past that many real seconds. Uses the real wall clock (<see cref="DateTimeOffset.UtcNow"/>),
/// not an injected <c>IClock</c>, the same "short real delay, not a fake clock" shape
/// <c>OperatorDisconnectGraceEndToEndTests</c> already uses for its own `GracePeriod`.</summary>
public sealed class TokenBucketFakeRateLimiter : IRateLimiter
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, (double Tokens, DateTimeOffset LastRefill)> buckets = [];

    public Task<RateLimitDecision> CheckAsync(RateLimitKey key, RateLimitRule rule, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            double tokens = rule.Capacity;
            if (buckets.TryGetValue(key.Value, out var bucket))
            {
                var elapsedSeconds = (now - bucket.LastRefill).TotalSeconds;
                tokens = Math.Min(rule.Capacity, bucket.Tokens + elapsedSeconds * rule.RefillPerSecond);
            }

            if (tokens >= 1)
            {
                buckets[key.Value] = (tokens - 1, now);
                return Task.FromResult(new RateLimitDecision(true, TimeSpan.Zero));
            }

            buckets[key.Value] = (tokens, now);
            var secondsUntilNextToken = (1 - tokens) / rule.RefillPerSecond;
            return Task.FromResult(new RateLimitDecision(false, TimeSpan.FromSeconds(secondsUntilNextToken)));
        }
    }
}
