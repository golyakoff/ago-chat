using Ago.Chat.Application.UseCases.GetSiteByPublicKey;
using Ago.Chat.Application.UseCases.UpdateWidgetConfig;
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
/// `11-01`'s own Done-when: a config write, followed by a fresh handshake read for that site, returns
/// the new values - proving the cache was actually invalidated end-to-end, not merely eventually
/// correct once `caching.md`'s 5-minute TTL expires. Real Postgres (the write and its outbox row),
/// real RabbitMQ (the same `OutboxDispatcher` -> `SiteCacheInvalidationConsumer` ->
/// `Ago.Platform.Caching.Redis.CacheInvalidationConsumer` chain `Ago.Chat.Worker`/`Ago.Chat.Api` run in
/// production, wired here by hand the same way `TracingEndToEndTests` wires its own pipeline stages),
/// real Redis (the actual cache entry `GetSiteConfigByPublicKeyHandler`'s cache-aside read populates
/// and the invalidation chain must actually evict).
///
/// This item's own first real producer for `SiteSettingsChanged` is what makes this test possible at
/// all - `SiteCacheInvalidationConsumer` has been written and tested against the contract directly
/// since `3-04` with nothing real to trigger it until `UpdateWidgetConfigHandler`.
/// </summary>
[Collection(ConnectionFanoutCollection.Name)]
public sealed class WidgetConfigCacheInvalidationEndToEndTests(ConnectionFanoutFixture fixture)
{
    [Fact]
    public async Task UpdatingWidgetConfig_InvalidatesTheCachedHandshakeRead_SoTheNextHandshakeSeesTheNewValue()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var publicKey = $"site_{siteId.Value:N}";
        var operatorId = new OperatorId(Guid.NewGuid());
        var roleId = Guid.NewGuid();

        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, publicKey, ["https://example.com"]));
            seed.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
            seed.Roles.Add(new RoleRecord
            {
                Id = roleId,
                SiteId = siteId,
                Name = "Operator",
                Permissions = [Permission.SiteConfigure.Value],
            });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        var cache = new RedisCache(
            fixture.RedisMultiplexer, new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(2)).Build(), NullLogger<RedisCache>.Instance);

        // The widget's own handshake read, cold - populates caching.md's "Site config" cache entry
        // (GetSiteConfigByPublicKeyHandler's own cache-aside shape, unchanged by this item).
        await using (var readDb = fixture.CreateDbContext())
        {
            var getSite = new GetSiteConfigByPublicKeyHandler(new SiteRepository(readDb), cache);
            var first = await getSite.HandleAsync(new GetSiteConfigByPublicKey(publicKey), CancellationToken.None);
            Assert.NotNull(first);
            Assert.Null(first.WidgetPrimaryColorHex);
            Assert.Equal(Position.BottomRight, first.WidgetPosition);
            Assert.Equal(Locale.En, first.WidgetLocale);
            Assert.Null(first.WidgetNoticeText);
            Assert.Null(first.WidgetNoticeUrl);
        }

        // The real chain: OutboxDispatcher (Ago.Chat.Worker) -> RabbitMQ -> SiteCacheInvalidationConsumer
        // (Ago.Chat.Worker, maps SiteSettingsChanged to a CacheInvalidated broadcast, adr/0020) ->
        // RabbitMQ -> Ago.Platform.Caching.Redis.CacheInvalidationConsumer (the same hosted service
        // Ago.Chat.Api registers) -> ICache.RemoveAsync on the real Redis key.
        await using var dispatcherConnection = fixture.CreateRabbitMqConnection();
        var dispatcher = new OutboxDispatcher(
            fixture.DataSource, new RabbitMqEventPublisher(dispatcherConnection), new SystemClock(),
            Options.Create(new OutboxDispatcherOptions { PollInterval = TimeSpan.FromMilliseconds(500) }), NullLogger<OutboxDispatcher>.Instance);

        await using var siteCacheConsumerConnection = fixture.CreateRabbitMqConnection();
        await using var siteCachePublisherConnection = fixture.CreateRabbitMqConnection();
        var siteCacheInvalidationConsumer = new SiteCacheInvalidationConsumer(
            new RabbitMqEventConsumer(siteCacheConsumerConnection),
            new CacheInvalidationPublisher(new RabbitMqEventPublisher(siteCachePublisherConnection), new SystemClock()),
            Options.Create(new SiteCacheInvalidationConsumerOptions()), NullLogger<SiteCacheInvalidationConsumer>.Instance);

        await using var cacheInvalidationConsumerConnection = fixture.CreateRabbitMqConnection();
        var cacheInvalidationConsumer = new CacheInvalidationConsumer(
            new RabbitMqEventConsumer(cacheInvalidationConsumerConnection), cache, NullLogger<CacheInvalidationConsumer>.Instance);

        await dispatcher.StartAsync(CancellationToken.None);
        await siteCacheInvalidationConsumer.StartAsync(CancellationToken.None);
        await cacheInvalidationConsumer.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(500)); // subscriptions to actually land - see NodeFanoutTests' own precedent

        try
        {
            await using (var writeDb = fixture.CreateDbContext())
            {
                var updateHandler = new UpdateWidgetConfigHandler(
                    new SiteRepository(writeDb), new PermissionChecker(writeDb), new EfOutboxWriter<AgoChatDbContext>(writeDb),
                    new UuidV7Generator(), new SystemClock());

                var updated = await updateHandler.HandleAsync(
                    new UpdateWidgetConfig(
                        siteId, operatorId, "#ff8800", nameof(Position.BottomLeft), nameof(Locale.Ru),
                        "We read what you send us.", "https://tenant.example/privacy"),
                    CancellationToken.None);
                Assert.True(updated.IsSuccess, updated.IsFailure ? updated.Error!.Value.Message : null);
            }

            // Polling the handshake read itself, not the Redis key directly - what `11-01`'s own
            // Done-when actually promises a caller sees, matching this suite's "assert observable
            // behaviour" convention (testing.md). `11-10`: the locale field rides the same cache
            // entry and the same invalidation chain, so it is asserted here rather than in a second,
            // near-duplicate end-to-end test. `16-04`: the two notice fields ride the identical chain -
            // one more reason this test stays one test rather than four near-duplicates.
            var sawNewValue = await OutboxTestHelpers.WaitUntilAsync(async () =>
            {
                await using var readDb = fixture.CreateDbContext();
                var getSite = new GetSiteConfigByPublicKeyHandler(new SiteRepository(readDb), cache);
                var read = await getSite.HandleAsync(new GetSiteConfigByPublicKey(publicKey), CancellationToken.None);
                return read is
                {
                    WidgetPrimaryColorHex: "#ff8800",
                    WidgetPosition: Position.BottomLeft,
                    WidgetLocale: Locale.Ru,
                    WidgetNoticeText: "We read what you send us.",
                    WidgetNoticeUrl: "https://tenant.example/privacy",
                };
            }, TimeSpan.FromSeconds(15));

            Assert.True(sawNewValue, "Timed out waiting for a fresh handshake read to see the updated widget config.");
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
            await siteCacheInvalidationConsumer.StopAsync(CancellationToken.None);
            await cacheInvalidationConsumer.StopAsync(CancellationToken.None);
        }
    }
}
