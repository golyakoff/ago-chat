using Ago.Chat.Api.Http;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `ago-root#353`: <see cref="RateLimitRetryAfter.Conservative"/> in isolation - no handler, no
/// hosting pipeline, the same level `DemoEndpointErrorStatusMappingTests` already tests
/// `ErrorExtensions.ToProblem`'s mapping at, since the thing under test is the pure computation
/// itself.
/// </summary>
public sealed class RateLimitRetryAfterTests
{
    [Fact]
    public void Conservative_OneBucket_IsTheReciprocalOfItsRefillRate()
    {
        // Capacity is irrelevant to this computation - only the refill rate bounds the wait, and
        // RedisRateLimiter's own script confirms it (`retry_after = (1 - tokens) / refill_per_second`,
        // tokens in [0, 1) whenever denied).
        var retryAfter = RateLimitRetryAfter.Conservative(1.0 / 3600);

        Assert.Equal(TimeSpan.FromSeconds(3600), retryAfter);
    }

    [Fact]
    public void Conservative_SeveralBuckets_ReturnsTheSlowestOnesOwnWorstCase()
    {
        // Three tiers refilling at different rates (fastest to slowest) - the slowest (smallest rate,
        // largest 1/rate) must win, because Error carries no marker for which tier actually denied
        // the call (RateLimitRetryAfter's own remarks on why "the max across buckets" is the only
        // answer that is never too short).
        var retryAfter = RateLimitRetryAfter.Conservative(100.0 / 3600, 5.0 / 3600, 3.0 / 3600);

        Assert.Equal(TimeSpan.FromSeconds(3600.0 / 3.0), retryAfter);
    }

    [Fact]
    public void Conservative_OrderOfArgumentsDoesNotMatter()
    {
        var ascending = RateLimitRetryAfter.Conservative(3.0 / 3600, 5.0 / 3600, 100.0 / 3600);
        var descending = RateLimitRetryAfter.Conservative(100.0 / 3600, 5.0 / 3600, 3.0 / 3600);

        Assert.Equal(ascending, descending);
    }

    [Fact]
    public void Conservative_NoRates_Throws()
    {
        Assert.Throws<ArgumentException>(() => RateLimitRetryAfter.Conservative());
    }
}
