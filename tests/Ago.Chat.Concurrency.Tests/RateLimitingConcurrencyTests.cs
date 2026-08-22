using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
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
        // A separate conversation per call: this proves the rate limiter's own atomicity, not
        // Conversation's optimistic-concurrency behaviour under many simultaneous writers to the
        // *same* aggregate - a real but different concern, already the case (and unhandled - a
        // DbUpdateConcurrencyException, not a Result failure) before this slice. One visitor with
        // several open conversations sharing one rate-limit bucket is the realistic shape anyway.
        var conversationIds = Enumerable.Range(0, 30).Select(_ => new ConversationId(Guid.NewGuid())).ToList();
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            foreach (var conversationId in conversationIds)
            {
                db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
            }

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

        var results = await Task.WhenAll(conversationIds.Select(async conversationId =>
        {
            await using var db = fixture.CreateDbContext();
            var handler = new SendVisitorMessageHandler(
                new ConversationRepository(db), new SystemClock(), new UuidV7Generator(), new EfOutboxWriter<Ago.Chat.Infrastructure.Postgres.Persistence.AgoChatDbContext>(db),
                limiter, options);
            return await handler.HandleAsync(new SendVisitorMessage(conversationId, visitorId, "hello"), CancellationToken.None);
        }));

        Assert.Equal(5, results.Count(r => r.IsSuccess));
        var denied = results.Where(r => r.IsFailure).ToList();
        Assert.Equal(25, denied.Count);
        Assert.All(denied, r => Assert.Equal("Message.RateLimited", r.Error!.Value.Code));
    }

    [Fact]
    public async Task ADeniedRequest_RetryAfterIsHonoured_WaitingThenRetryingSucceeds()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
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
        // 2 tokens/sec (0.5s to refill) - fast enough the test does not sleep long, slow enough that
        // it comfortably outlasts the real Postgres round trip between the first and second call
        // (a much faster refill made the second call flaky: it could complete after enough of a
        // token had already regenerated to be allowed too).
        var options = new MessageSendRateLimitOptions
        {
            PerVisitorCapacity = 1,
            PerVisitorRefillPerSecond = 2,
            PerSiteCapacity = 1000,
            PerSiteRefillPerSecond = 1000,
        };

        async Task<Ago.Platform.Kernel.Result<int>> SendAsync(string body)
        {
            await using var db = fixture.CreateDbContext();
            var handler = new SendVisitorMessageHandler(
                new ConversationRepository(db), new SystemClock(), new UuidV7Generator(),
                new EfOutboxWriter<Ago.Chat.Infrastructure.Postgres.Persistence.AgoChatDbContext>(db), limiter, options);
            return await handler.HandleAsync(new SendVisitorMessage(conversationId, visitorId, body), CancellationToken.None);
        }

        var firstResult = await SendAsync("one");
        Assert.True(firstResult.IsSuccess);

        var deniedResult = await SendAsync("two");
        Assert.True(deniedResult.IsFailure);

        // The retry-after rides in the message text (Ago.Platform.Kernel.Error has no structured
        // field) - re-derive the wait from the same rule directly rather than parsing it back out
        // of a human-readable string.
        var retryAfter = TimeSpan.FromSeconds(1.0 / options.PerVisitorRefillPerSecond);
        await Task.Delay(retryAfter + TimeSpan.FromMilliseconds(50));

        var afterWaitingResult = await SendAsync("three");
        Assert.True(afterWaitingResult.IsSuccess);
    }
}
