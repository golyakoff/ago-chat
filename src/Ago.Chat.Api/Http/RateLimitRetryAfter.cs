namespace Ago.Chat.Api.Http;

/// <summary>
/// `ago-root#353`: the conservative <c>Retry-After</c> estimate every rate-limited HTTP endpoint in
/// this product uses - derived from configuration an endpoint already holds, never from asking
/// <c>IRateLimiter</c> a second time. A second check per request would consume another token from the
/// same bucket the owning use case's handler already checked (`RedisRateLimiter`'s Lua script only
/// decrements on an *allowed* check, so a denied check that ran twice would silently halve the
/// configured limit) - the trap this item's own backlog file records before anyone started. None of
/// the five endpoints that call <see cref="Conservative"/> hold an <see cref="Ago.Platform.Abstractions.IRateLimiter"/>
/// reference at all, which is the strongest proof available that they cannot fall into it.
///
/// <para><b>Why configuration, not a value threaded out of <c>Ago.Platform.Kernel.Error</c>.</b> Error
/// is <c>(Code, Message)</c> - a widening lives in the platform package every product ships, and
/// `ago-root#353`'s own item file rules that out from a single product's five call sites. The
/// alternative this class replaces - `ago-root#347`'s `DemoTenantErrors.TryGetRateLimitedRetryAfterSeconds`,
/// reading a marker back out of the error's own message - works for one code with one producer; doing
/// it five more times was the same item's own "least honest of the three" verdict, restated by having
/// five near-identical parsers instead of one. This class needs no round trip through prose: every
/// `*RateLimitOptions` value an endpoint's own handler already used to make its decision is already
/// sitting in the same DI container the endpoint resolves from, registered as a plain singleton value
/// (`Ago.Chat.Module.ChatModule`'s own <c>services.AddSingleton(sp =&gt; sp.GetRequiredService&lt;IOptions&lt;T&gt;&gt;().Value)</c>
/// line for each one) - so an endpoint that wants to render a header just asks for the same options
/// object its handler already holds.</para>
///
/// <para><b>Why the slowest bucket, not the one that actually denied the request.</b> The <see
/// cref="Ago.Platform.Kernel.Error"/> a handler returns carries no marker for which of its several
/// tiers (visitor/operator/site, phone/visitor/site, ...) rejected the call - by design, since that is
/// exactly the structured detail Error does not model. Taking the slowest-refilling bucket's own
/// worst case is therefore the only answer that is never too short: whichever bucket actually denied
/// the request, its own wait is at most this one. "Slightly pessimistic, never premature" is the
/// deliberate trade the item's own backlog file names, not an approximation error.</para>
/// </summary>
public static class RateLimitRetryAfter
{
    /// <summary>
    /// The worst-case wait for a token-bucket limiter refilling at <paramref name="refillRatesPerSecond"/>
    /// (one entry per configured bucket a caller's use case checks) to have a token again, assuming
    /// the bucket had just been emptied. <c>RedisRateLimiter</c>'s own Lua script never lets a denied
    /// check's remaining tokens fall below <c>0</c> - <c>retry_after = (1 - tokens) / refill_per_second</c>
    /// with <c>tokens</c> in <c>[0, 1)</c> - so <c>1 / rate</c> is a tight upper bound for one bucket,
    /// and the maximum across buckets remains a safe upper bound regardless of which one actually
    /// denied the request.
    /// </summary>
    public static TimeSpan Conservative(params double[] refillRatesPerSecond)
    {
        if (refillRatesPerSecond.Length == 0)
        {
            throw new ArgumentException("At least one refill rate is required.", nameof(refillRatesPerSecond));
        }

        var worstSeconds = 0.0;
        foreach (var rate in refillRatesPerSecond)
        {
            worstSeconds = Math.Max(worstSeconds, 1.0 / rate);
        }

        return TimeSpan.FromSeconds(worstSeconds);
    }
}
