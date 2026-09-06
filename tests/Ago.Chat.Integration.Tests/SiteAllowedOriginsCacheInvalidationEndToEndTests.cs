using Ago.Chat.Application.UseCases.CheckCorsOrigin;
using Ago.Chat.Application.UseCases.GetSiteByPublicKey;
using Ago.Chat.Application.UseCases.GetSiteConfigById;
using Ago.Chat.Application.UseCases.UpdateSiteAllowedOriginsAsOwner;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Worker;
using Ago.Platform.Caching.Redis;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Messaging.RabbitMq;
using Ago.Platform.Persistence.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-48`'s own hardest Done-when, demonstrated rather than asserted (this item's own brief: "what
/// must be demonstrated rather than asserted"): a changed allowed-origins list takes effect on the
/// very next request, against <b>all three</b> cache shapes a write to this field touches - proven
/// here in one test, not assumed from any one of them:
///
/// <list type="bullet">
/// <item><see cref="Application.Caching.SiteCacheKeys.ForPublicKey"/> - the widget handshake's own
/// cached <c>SiteConfigDto</c> (<see cref="GetSiteConfigByPublicKeyHandler"/>).</item>
/// <item><see cref="Application.Caching.SiteCacheKeys.ForSiteId"/> - the identical row, keyed for a
/// hub connection holding only a JWT `site_id` claim (<see cref="GetSiteConfigByIdHandler"/>).</item>
/// <item><see cref="Application.Caching.CorsOriginCacheKeys.ForOrigin"/> - the CORS preflight's own
/// layer-1 check (<see cref="CheckCorsOriginHandler"/>), keyed by origin string rather than by site -
/// the cache shape neither <see cref="SiteCacheInvalidationConsumer"/> nor
/// <see cref="WidgetConfigCacheInvalidationEndToEndTests"/> has ever touched before this item, and
/// this file's own reason to exist.</item>
/// </list>
///
/// <para>The first two ride the identical, already-proven chain
/// <see cref="WidgetConfigCacheInvalidationEndToEndTests"/> wires for a different <see cref="Site"/>
/// write (<c>SiteSettingsChanged</c> -> <see cref="SiteCacheInvalidationConsumer"/>) - reused here
/// unchanged, not re-derived, and wired into this test rather than assumed from that other file's own
/// pass, because <c>SiteAllowedOriginsUpdatedMapper</c> is a new producer of that same contract and a
/// new producer is exactly the kind of wiring mistake (forgot to enqueue, wrong SiteId) a passing
/// unit test - which only inspects the outbox, never a real cache - cannot catch.</para>
///
/// <para>Real Postgres (the write and its two outbox rows), real RabbitMQ (<c>OutboxDispatcher</c> ->
/// both <see cref="SiteCacheInvalidationConsumer"/> and
/// <see cref="SiteAllowedOriginsCacheInvalidationConsumer"/>, run side by side against the one write ->
/// <c>Ago.Platform.Caching.Redis.CacheInvalidationConsumer</c>), real Redis (every cache entry below
/// is read and evicted for real, never mocked).</para>
///
/// <para><b>Proves both directions a write can leave stale</b> for the CORS-layer cache specifically:
/// the origin being removed keeps answering "allowed" for up to
/// <see cref="CheckCorsOriginHandler"/>'s own 5-minute positive TTL unless evicted; the origin being
/// added may already carry a negative "denied" entry from an earlier check, which without eviction
/// would keep answering "denied" for up to the shorter 30-second negative TTL. This test seeds both
/// cache states before the write, so a consumer that only evicted one list (the bug
/// <c>SiteAllowedOriginsUpdated</c>'s own remarks warn against) would fail it.</para>
/// </summary>
[Collection(ConnectionFanoutCollection.Name)]
public sealed class SiteAllowedOriginsCacheInvalidationEndToEndTests(ConnectionFanoutFixture fixture)
{
    [Fact]
    public async Task UpdatingAllowedOrigins_InvalidatesAllThreeCacheShapes_Immediately()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var publicKey = $"site_{siteId.Value:N}";
        const string removedOrigin = "https://old-tenant-domain.example";
        const string addedOrigin = "https://new-tenant-domain.example";

        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, publicKey, [removedOrigin]));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        var cache = new RedisCache(
            fixture.RedisMultiplexer, new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(2)).Build(), NullLogger<RedisCache>.Instance);

        await using (var readDb1 = fixture.CreateDbContext())
        await using (var readDb2 = fixture.CreateDbContext())
        await using (var readDb3 = fixture.CreateDbContext())
        {
            var checkOrigin = new CheckCorsOriginHandler(new SiteRepository(readDb1), cache);

            // Populate the positive CORS entry for the origin about to be removed - the layer-1
            // preflight check any of this tenant's visitors already triggered before the write.
            var removedAllowedBefore = await checkOrigin.HandleAsync(new CheckOriginAllowed(removedOrigin), CancellationToken.None);
            Assert.True(removedAllowedBefore);

            // Populate the negative CORS entry for the origin about to be added - a preflight from
            // the tenant's new domain, checked once before the owner's write, when no site allowed it
            // yet.
            var addedAllowedBefore = await checkOrigin.HandleAsync(new CheckOriginAllowed(addedOrigin), CancellationToken.None);
            Assert.False(addedAllowedBefore);

            // Populate both site-config cache entries too - the widget handshake's own read
            // (ForPublicKey) and the hub's own read (ForSiteId) - both must still be showing the old
            // AllowedOrigins right now, or the "before" half of this test proves nothing.
            var getByPublicKey = new GetSiteConfigByPublicKeyHandler(new SiteRepository(readDb2), cache);
            var byPublicKeyBefore = await getByPublicKey.HandleAsync(new GetSiteConfigByPublicKey(publicKey), CancellationToken.None);
            Assert.NotNull(byPublicKeyBefore);
            Assert.Equal([removedOrigin], byPublicKeyBefore.AllowedOrigins);

            var getById = new GetSiteConfigByIdHandler(new SiteRepository(readDb3), cache);
            var byIdBefore = await getById.HandleAsync(new GetSiteConfigById(siteId), CancellationToken.None);
            Assert.NotNull(byIdBefore);
            Assert.Equal([removedOrigin], byIdBefore.AllowedOrigins);
        }

        // The real chain, both halves: OutboxDispatcher -> RabbitMQ ->
        // { SiteCacheInvalidationConsumer (site-config cache), SiteAllowedOriginsCacheInvalidationConsumer
        // (CORS-layer cache) } -> RabbitMQ -> Ago.Platform.Caching.Redis.CacheInvalidationConsumer ->
        // ICache.RemoveAsync on the real Redis key. The first of the two is the identical shape
        // WidgetConfigCacheInvalidationEndToEndTests wires for a different write; the second is new in
        // this item.
        await using var dispatcherConnection = fixture.CreateRabbitMqConnection();
        var dispatcher = new OutboxDispatcher(
            fixture.DataSource, new RabbitMqEventPublisher(dispatcherConnection, NullLogger<RabbitMqEventPublisher>.Instance), new SystemClock(),
            Options.Create(new OutboxDispatcherOptions { PollInterval = TimeSpan.FromMilliseconds(500) }), NullLogger<OutboxDispatcher>.Instance);

        await using var siteCacheConsumerConnection = fixture.CreateRabbitMqConnection();
        await using var siteCachePublisherConnection = fixture.CreateRabbitMqConnection();
        var siteCacheInvalidationConsumer = new SiteCacheInvalidationConsumer(
            new RabbitMqEventConsumer(siteCacheConsumerConnection),
            new CacheInvalidationPublisher(new RabbitMqEventPublisher(siteCachePublisherConnection, NullLogger<RabbitMqEventPublisher>.Instance), new SystemClock()),
            Options.Create(new SiteCacheInvalidationConsumerOptions()), NullLogger<SiteCacheInvalidationConsumer>.Instance);

        await using var originsCacheConsumerConnection = fixture.CreateRabbitMqConnection();
        await using var originsCachePublisherConnection = fixture.CreateRabbitMqConnection();
        var originsCacheInvalidationConsumer = new SiteAllowedOriginsCacheInvalidationConsumer(
            new RabbitMqEventConsumer(originsCacheConsumerConnection),
            new CacheInvalidationPublisher(new RabbitMqEventPublisher(originsCachePublisherConnection, NullLogger<RabbitMqEventPublisher>.Instance), new SystemClock()),
            Options.Create(new SiteAllowedOriginsCacheInvalidationConsumerOptions()), NullLogger<SiteAllowedOriginsCacheInvalidationConsumer>.Instance);

        await using var cacheInvalidationConsumerConnection = fixture.CreateRabbitMqConnection();
        var cacheInvalidationConsumer = new CacheInvalidationConsumer(
            new RabbitMqEventConsumer(cacheInvalidationConsumerConnection), cache, NullLogger<CacheInvalidationConsumer>.Instance);

        using var management = fixture.CreateRabbitMqManagementClient();
        var cacheInvalidatedQueueNamesBeforeStart = await RabbitMqSubscriptionTestHelpers.GetQueueNamesBoundToExchangeAsync(
            management, CacheTopics.Invalidated, CancellationToken.None);

        await dispatcher.StartAsync(CancellationToken.None);
        await siteCacheInvalidationConsumer.StartAsync(CancellationToken.None);
        await originsCacheInvalidationConsumer.StartAsync(CancellationToken.None);
        await cacheInvalidationConsumer.StartAsync(CancellationToken.None);

        await RabbitMqSubscriptionTestHelpers.AwaitAllCompetingSubscriptionsAsync(
            management, TimeSpan.FromSeconds(10),
            (nameof(Contracts.SiteSettingsChanged), SiteCacheInvalidationConsumer.ConsumerName),
            (nameof(Contracts.SiteAllowedOriginsChanged), SiteAllowedOriginsCacheInvalidationConsumer.ConsumerName));
        var cacheInvalidationLanded = await RabbitMqSubscriptionTestHelpers.WaitForNewBroadcastSubscriptionAsync(
            management, CacheTopics.Invalidated, cacheInvalidatedQueueNamesBeforeStart, TimeSpan.FromSeconds(10));
        Assert.True(cacheInvalidationLanded,
            $"The Broadcast cache-invalidation subscription to '{CacheTopics.Invalidated}' never landed - no new " +
            "queue bound to its exchange ever reached a live consumer within 10s.");

        try
        {
            await using (var writeDb = fixture.CreateDbContext())
            {
                var updateHandler = new UpdateSiteAllowedOriginsAsOwnerHandler(
                    new SiteRepository(writeDb), new EfOutboxWriter<AgoChatDbContext>(writeDb), new UuidV7Generator(), new SystemClock());

                var updated = await updateHandler.HandleAsync(
                    new UpdateSiteAllowedOriginsAsOwner(siteId, [addedOrigin]), CancellationToken.None);
                Assert.True(updated.IsSuccess, updated.IsFailure ? updated.Error!.Value.Message : null);
            }

            // Polling the real reads themselves, not the Redis keys directly - what a caller actually
            // observes. All three must reflect the new value on the very next call - no restart, no
            // waiting out any TTL.
            await using var pollDb1 = fixture.CreateDbContext();
            var pollCheck = new CheckCorsOriginHandler(new SiteRepository(pollDb1), cache);

            var removedNowDenied = await OutboxTestHelpers.WaitUntilAsync(
                async () => !await pollCheck.HandleAsync(new CheckOriginAllowed(removedOrigin), CancellationToken.None),
                TimeSpan.FromSeconds(15));
            Assert.True(removedNowDenied, "Timed out waiting for the removed origin's CORS cache entry (CorsOriginCacheKeys.ForOrigin) to be invalidated.");

            var addedNowAllowed = await OutboxTestHelpers.WaitUntilAsync(
                async () => await pollCheck.HandleAsync(new CheckOriginAllowed(addedOrigin), CancellationToken.None),
                TimeSpan.FromSeconds(15));
            Assert.True(addedNowAllowed, "Timed out waiting for the added origin's CORS cache entry (CorsOriginCacheKeys.ForOrigin) to be invalidated.");

            await using var pollDb2 = fixture.CreateDbContext();
            var pollByPublicKey = new GetSiteConfigByPublicKeyHandler(new SiteRepository(pollDb2), cache);
            var publicKeySawNewValue = await OutboxTestHelpers.WaitUntilAsync(async () =>
            {
                var read = await pollByPublicKey.HandleAsync(new GetSiteConfigByPublicKey(publicKey), CancellationToken.None);
                return read is { AllowedOrigins: [var only] } && only == addedOrigin;
            }, TimeSpan.FromSeconds(15));
            Assert.True(publicKeySawNewValue, "Timed out waiting for SiteCacheKeys.ForPublicKey to be invalidated.");

            await using var pollDb3 = fixture.CreateDbContext();
            var pollById = new GetSiteConfigByIdHandler(new SiteRepository(pollDb3), cache);
            var idSawNewValue = await OutboxTestHelpers.WaitUntilAsync(async () =>
            {
                var read = await pollById.HandleAsync(new GetSiteConfigById(siteId), CancellationToken.None);
                return read is { AllowedOrigins: [var only] } && only == addedOrigin;
            }, TimeSpan.FromSeconds(15));
            Assert.True(idSawNewValue, "Timed out waiting for SiteCacheKeys.ForSiteId to be invalidated.");
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
            await siteCacheInvalidationConsumer.StopAsync(CancellationToken.None);
            await originsCacheInvalidationConsumer.StopAsync(CancellationToken.None);
            await cacheInvalidationConsumer.StopAsync(CancellationToken.None);
        }
    }
}
