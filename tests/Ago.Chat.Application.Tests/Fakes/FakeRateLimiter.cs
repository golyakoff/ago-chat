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
