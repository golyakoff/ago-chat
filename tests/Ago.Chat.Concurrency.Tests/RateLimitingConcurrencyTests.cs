using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Platform.Abstractions;
using Ago.Platform.Caching.Redis;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>`3-05`'s Done-when: the atomic-check-and-decrement claim, proven under actual
/// concurrency, the same way `2-05`'s idempotency and `3-04`'s stampede protection are proven -
/// real concurrency, not a sequential loop. Reuses <see cref="SiteCachingConcurrencyFixture"/>
/// (Postgres + Redis) rather than a third near-identical fixture.</summary>
[Collection(SiteCachingConcurrencyCollection.Name)]
public sealed class RateLimitingConcurrencyTests(SiteCachingConcurrencyFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ManyConcurrentSends_AgainstOneVisitorsBucket_AllowExactlyCapacityAndDenyTheRest()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        // `25-68`: one open conversation, not thirty - ix_conversations_one_open_per_visitor now
        // refuses a second one for the same visitor, so "one visitor with several open conversations
        // sharing one rate-limit bucket" (this test's own earlier claim) is no longer a shape the
        // system can reach at all. Every send below targets this one conversation instead.
        //
        // **This changes what the test can honestly assert about the *write* side, not the rate
        // limiter.** The rate limiter's own decision (exactly `PerVisitorCapacity` allowed, the rest
        // denied) is still atomic and still asserted exactly - proven separately by retrying
        // `HandleAsync` on failure would be wrong here, because a retry re-runs the rate check too
        // (`SendVisitorMessageHandler`'s own remarks: it checks the limiter before it ever loads the
        // conversation), silently spending a second token for a request that already spent its first
        // one. What is no longer safe to assert exactly is that all five *allowed* requests also win
        // their write: up to `PerVisitorCapacity` real concurrent writers can now land on the one real
        // conversation row this scenario has, which is precisely the "Conversation's own
        // optimistic-concurrency behaviour under many simultaneous writers" this test's own original
        // design deliberately avoided conflating with rate-limiting - unavoidable now that a visitor
        // can hold only one open conversation. `MessageBatchWriter`'s own answer for that race is
        // `Message.Unavailable` ("try again"), not a crash - so every non-`RateLimited` failure is
        // asserted to be exactly that, accounted for, never silently swallowed.
        var conversationId = new ConversationId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
            await db.SaveChangesAsync();
        }

        var limiter = new RedisRateLimiter(
            fixture.RedisMultiplexer, new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(2)).Build(), NullLogger<RedisRateLimiter>.Instance);
        // Capacity 5, refill slow enough that none of it refills meaningfully during the burst below.
        var options = new MessageSendRateLimitOptions
        {
            PerVisitorCapacity = 5,
            PerVisitorRefillPerSecond = 0.001,
            PerSiteCapacity = 1000,
            PerSiteRefillPerSecond = 1000,
        };

        var results = await Task.WhenAll(Enumerable.Range(0, 30).Select(async _ =>
        {
            await using var db = fixture.CreateDbContext();
            var handler = new SendVisitorMessageHandler(
                new ConversationRepository(db), limiter, options, new SynchronousMessagePipeline(fixture.DataSource));
            return await handler.HandleAsync(new SendVisitorMessage(conversationId, visitorId, "hello"), CancellationToken.None);
        }));

        // The rate limiter's own claim, exact: it let through exactly PerVisitorCapacity and denied
        // everything else - the atomicity `3-05` exists to prove, unaffected by what happens to an
        // allowed request afterward.
        var rateLimited = results.Count(r => r.IsFailure && r.Error!.Value.Code == "Message.RateLimited");
        Assert.Equal(25, rateLimited);
        var admitted = results.Where(r => r.IsSuccess || r.Error!.Value.Code != "Message.RateLimited").ToList();
        Assert.Equal(5, admitted.Count);

        // Of the five the limiter admitted, every one either actually wrote its message or lost a
        // real, explained write race - never anything else.
        Assert.All(admitted, r => Assert.True(r.IsSuccess || r.Error!.Value.Code == "Message.Unavailable"));
        // At least one message really went through - the pipeline is not simply failing all five.
        Assert.Contains(admitted, r => r.IsSuccess);
    }

    [Fact]
    public async Task ADeniedRequest_RetryAfterIsHonoured_WaitingThenRetryingSucceeds()
    {
        // Checks IRateLimiter directly rather than through SendVisitorMessageHandler - the wiring
        // through the handler is Ago.Chat.Integration.Tests.RateLimitingTests' job; this test is
        // specifically about the timing contract (deny now, allow again after RetryAfter), which a
        // real Postgres round trip between calls would put at the mercy of CI-runner latency
        // variance sitting in the same window as the bucket's own refill - found flaky in CI for
        // exactly that reason (a slower-than-local round trip let enough of a token regenerate
        // between the first and second call to allow it too). Waiting the exact RetryAfter Redis
        // itself returned, not a value re-derived from the configured rate, removes the guesswork.
        var limiter = new RedisRateLimiter(
            fixture.RedisMultiplexer, new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(2)).Build(), NullLogger<RedisRateLimiter>.Instance);
        var key = new RateLimitKey($"test:{Guid.NewGuid():N}");
        var rule = new RateLimitRule(Capacity: 1, RefillPerSecond: 5);

        var first = await limiter.CheckAsync(key, rule, CancellationToken.None);
        Assert.True(first.Allowed);

        var denied = await limiter.CheckAsync(key, rule, CancellationToken.None);
        Assert.False(denied.Allowed);

        await Task.Delay(denied.RetryAfter + TimeSpan.FromMilliseconds(100));

        var afterWaiting = await limiter.CheckAsync(key, rule, CancellationToken.None);
        Assert.True(afterWaiting.Allowed);
    }
}
