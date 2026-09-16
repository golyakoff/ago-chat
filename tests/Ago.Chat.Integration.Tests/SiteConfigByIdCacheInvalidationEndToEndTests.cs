using Ago.Chat.Application.UseCases.GetSiteConfigById;
using Ago.Chat.Application.UseCases.UpdateWidgetConfig;
using Ago.Chat.Contracts;
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
/// `25-106`: the id-keyed twin of <see cref="WidgetConfigCacheInvalidationEndToEndTests"/> - that test
/// proves the invalidation chain end to end for <see cref="GetSiteConfigByPublicKeyHandler"/>'s own
/// cache entry (the widget handshake read), but nothing exercised
/// <see cref="GetSiteConfigByIdHandler"/>'s own <c>site-config:id:{siteId}</c> entry - the one
/// <c>StartConversationHandler</c> actually reads to seed a brand-new conversation's attachment-upload
/// grant. `SiteCacheInvalidationConsumer` has published an invalidation for both keys since `14-04`,
/// but nothing proved the second one actually lands.
///
/// It does: this test is the reproduction of `25-106`'s own finding, run through the real write path
/// (`UpdateWidgetConfigHandler`) instead of the raw `UPDATE sites ...` the live investigation used to
/// isolate the question from `25-104`'s own code. The live symptom does not reproduce here - the raw
/// SQL bypass is what skipped invalidation (it publishes no `SiteSettingsChanged` for anything to
/// react to), not a defect in this chain. `25-106`'s own item file has the rest of that reasoning.
/// </summary>
[Collection(ConnectionFanoutCollection.Name)]
public sealed class SiteConfigByIdCacheInvalidationEndToEndTests(ConnectionFanoutFixture fixture)
{
    [Fact]
    public async Task UpdatingWidgetConfig_InvalidatesTheIdKeyedCache_SoAFreshReadSeesTheNewValue()
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

        // Warm the id-keyed entry the way a visitor starting a conversation would, before the tenant
        // ever touches the console - the same order 25-106's own live investigation happened in.
        await using (var readDb = fixture.CreateDbContext())
        {
            var getSite = new GetSiteConfigByIdHandler(new SiteRepository(readDb), cache);
            var first = await getSite.HandleAsync(new GetSiteConfigById(siteId), CancellationToken.None);
            Assert.NotNull(first);
            Assert.False(first!.WidgetAllowAttachmentUploadsByDefault);
        }

        // The real chain, identical to WidgetConfigCacheInvalidationEndToEndTests's own wiring:
        // OutboxDispatcher -> RabbitMQ -> SiteCacheInvalidationConsumer -> RabbitMQ ->
        // Ago.Platform.Caching.Redis.CacheInvalidationConsumer -> ICache.RemoveAsync.
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

        await using var cacheInvalidationConsumerConnection = fixture.CreateRabbitMqConnection();
        var cacheInvalidationConsumer = new CacheInvalidationConsumer(
            new RabbitMqEventConsumer(cacheInvalidationConsumerConnection), cache, NullLogger<CacheInvalidationConsumer>.Instance);

        using var management = fixture.CreateRabbitMqManagementClient();
        var cacheInvalidatedQueueNamesBeforeStart = await RabbitMqSubscriptionTestHelpers.GetQueueNamesBoundToExchangeAsync(
            management, CacheTopics.Invalidated, CancellationToken.None);

        await dispatcher.StartAsync(CancellationToken.None);
        await siteCacheInvalidationConsumer.StartAsync(CancellationToken.None);
        await cacheInvalidationConsumer.StartAsync(CancellationToken.None);

        await RabbitMqSubscriptionTestHelpers.AwaitAllCompetingSubscriptionsAsync(
            management, TimeSpan.FromSeconds(10),
            (nameof(SiteSettingsChanged), SiteCacheInvalidationConsumer.ConsumerName));
        var cacheInvalidationLanded = await RabbitMqSubscriptionTestHelpers.WaitForNewBroadcastSubscriptionAsync(
            management, CacheTopics.Invalidated, cacheInvalidatedQueueNamesBeforeStart, TimeSpan.FromSeconds(10));
        Assert.True(cacheInvalidationLanded,
            $"The Broadcast cache-invalidation subscription to '{CacheTopics.Invalidated}' never landed - no new " +
            "queue bound to its exchange ever reached a live consumer within 10s.");

        try
        {
            await using (var writeDb = fixture.CreateDbContext())
            {
                var updateHandler = new UpdateWidgetConfigHandler(
                    new SiteRepository(writeDb), new PermissionChecker(writeDb), new EfOutboxWriter<AgoChatDbContext>(writeDb),
                    new UuidV7Generator(), new SystemClock());

                var updated = await updateHandler.HandleAsync(
                    new UpdateWidgetConfig(
                        siteId, operatorId, null, nameof(Position.BottomRight), nameof(Locale.En),
                        null, null, RequireContactConsent: false, AttractAttention: false,
                        AllowAttachmentUploadsByDefault: true),
                    CancellationToken.None);
                Assert.True(updated.IsSuccess, updated.IsFailure ? updated.Error!.Value.Message : null);
            }

            // Polling the handler's own observable read, matching WidgetConfigCacheInvalidationEndToEndTests'
            // "assert observable behaviour, not the Redis key directly" convention (testing.md).
            var sawNewValue = await OutboxTestHelpers.WaitUntilAsync(async () =>
            {
                await using var readDb = fixture.CreateDbContext();
                var getSite = new GetSiteConfigByIdHandler(new SiteRepository(readDb), cache);
                var read = await getSite.HandleAsync(new GetSiteConfigById(siteId), CancellationToken.None);
                return read is { WidgetAllowAttachmentUploadsByDefault: true };
            }, TimeSpan.FromSeconds(15));

            Assert.True(sawNewValue, "Timed out waiting for a fresh id-keyed read to see the updated attachment default.");
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
            await siteCacheInvalidationConsumer.StopAsync(CancellationToken.None);
            await cacheInvalidationConsumer.StopAsync(CancellationToken.None);
        }
    }
}
