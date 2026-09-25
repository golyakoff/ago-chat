using Ago.Platform.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Always allows - stands in for tests that need a rate-limited handler but are not
/// themselves testing rate limiting. <see cref="RateLimitedFakeRateLimiter"/> is the one that
/// actually denies, for the tests that are.</summary>
public sealed class FakeRateLimiter : IRateLimiter
{
    public Task<RateLimitDecision> CheckAsync(RateLimitKey key, RateLimitRule rule, CancellationToken cancellationToken) =>
        Task.FromResult(new RateLimitDecision(true, TimeSpan.Zero));
}

/// <summary>Always denies, with a fixed retry-after - the counterpart to
/// <see cref="FakeRateLimiter"/> for asserting the denied path.</summary>
public sealed class RateLimitedFakeRateLimiter(TimeSpan retryAfter) : IRateLimiter
{
    public Task<RateLimitDecision> CheckAsync(RateLimitKey key, RateLimitRule rule, CancellationToken cancellationToken) =>
        Task.FromResult(new RateLimitDecision(false, retryAfter));
}

/// <summary>Denies only keys containing <paramref name="denyKeyContains"/>, allows everything else -
/// for proving a *specific* bucket (e.g. the per-site one) is actually consulted, not just that some
/// bucket exists, without needing a real Redis token-bucket implementation.</summary>
public sealed class SelectiveFakeRateLimiter(string denyKeyContains, TimeSpan retryAfter) : IRateLimiter
{
    public Task<RateLimitDecision> CheckAsync(RateLimitKey key, RateLimitRule rule, CancellationToken cancellationToken) =>
        Task.FromResult(key.Value.Contains(denyKeyContains, StringComparison.Ordinal)
            ? new RateLimitDecision(false, retryAfter)
            : new RateLimitDecision(true, TimeSpan.Zero));
}

/// <summary>`26-120`: allows the first check for each distinct key and denies every later check for that
/// same key - the "claim once per window" contract `RedisRateLimiter` gives with <c>Capacity: 1</c>,
/// reduced to exactly what a handler-level test needs to tell "same notification repeated" (same key,
/// suppressed) from "new message in the same conversation" (new key, allowed) without a real Redis
/// token bucket. Records every key it was asked about so a test can assert which marker the handler
/// actually claimed.</summary>
public sealed class ClaimOncePerKeyFakeRateLimiter : IRateLimiter
{
    private readonly HashSet<string> _claimed = [];

    public List<string> Keys { get; } = [];

    public Task<RateLimitDecision> CheckAsync(RateLimitKey key, RateLimitRule rule, CancellationToken cancellationToken)
    {
        Keys.Add(key.Value);
        var firstClaim = _claimed.Add(key.Value);
        return Task.FromResult(new RateLimitDecision(firstClaim, firstClaim ? TimeSpan.Zero : TimeSpan.FromSeconds(1)));
    }
}

/// <summary>`26-120`: throws on every check - the marker store is unreachable. A handler that fails open
/// must send anyway (never drop a genuine first push); one that let this propagate would drop it.</summary>
public sealed class ThrowingFakeRateLimiter : IRateLimiter
{
    public Task<RateLimitDecision> CheckAsync(RateLimitKey key, RateLimitRule rule, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("marker store unreachable");
}
