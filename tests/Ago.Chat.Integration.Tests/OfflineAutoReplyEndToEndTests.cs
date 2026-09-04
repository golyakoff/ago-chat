using Ago.Chat.Application.UseCases.GetSiteConfigById;
using Ago.Chat.Application.UseCases.SendOfflineAutoReply;
using Ago.Chat.Application.UseCases.UpdateOfflineAutoReply;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `14-04` against real infrastructure. Three things here cannot be shown with fakes, and each is a
/// claim this item would otherwise only be asserting:
///
/// <list type="number">
/// <item><b>The reply really is one transaction with its inbox row.</b>
/// <c>FakeInboxChecker</c> mirrors the yes/no answer and nothing else - it has no transaction to roll
/// back - so "a redelivery leaves the row genuinely untouched" (`adr/0017`, `CLAUDE.md` rule 5) needs
/// real Postgres to mean anything.</item>
/// <item><b>The toggle is live config.</b> The item's Done-when is that flipping it from the console
/// changes behaviour with no rebuild. That runs through the real outbox -> RabbitMQ ->
/// <c>SiteCacheInvalidationConsumer</c> -> <c>CacheInvalidationConsumer</c> -> Redis chain, and it is
/// also the regression test for the half of that chain `14-04` had to fix: the id-keyed cache entry
/// was never being evicted at all.</item>
/// <item><b>The "is anybody on duty" predicate is real SQL</b>, and says <c>Online</c> without any
/// capacity term - which is exactly the difference between "the shop is closed" and "everyone is
/// busy".</item>
/// </list>
/// </summary>
[Collection(ConnectionFanoutCollection.Name)]
public sealed class OfflineAutoReplyEndToEndTests(ConnectionFanoutFixture fixture)
{
    private const string Fallback = "We are closed - we will reply in the morning.";

    private RedisCache CreateCache() => new(
        fixture.RedisMultiplexer,
        new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(2)).Build(),
        NullLogger<RedisCache>.Instance);

    [Fact]
    public async Task ARedeliveredTrigger_WritesExactlyOneReplyRow()
    {
        var (siteId, operatorId, publicKey) = await SeedSiteAsync(operatorStatus: OperatorStatus.Offline);
        await EnableAutoReplyAsync(siteId, operatorId);
        var (conversationId, triggerMessageId, triggerSequence) = await SeedWaitingConversationAsync(siteId, "hello?");

        var cache = CreateCache();
        var command = new SendOfflineAutoReply(
            triggerMessageId, siteId, conversationId, MessageAuthorKind.Visitor, triggerSequence);

        // Two separate scopes, exactly as two separate broker deliveries would be - each with its own
        // DbContext, so the second cannot see the first's change tracker.
        await using (var db = fixture.CreateDbContext())
        {
            var result = await CreateHandler(db, cache).HandleAsync(command, CancellationToken.None);
            Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : null);
            Assert.Equal(OfflineAutoReplyOutcome.Sent, result.Value);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var redelivery = await CreateHandler(db, cache).HandleAsync(command, CancellationToken.None);
            Assert.True(redelivery.IsSuccess);
            Assert.Equal(OfflineAutoReplyOutcome.AlreadyReplied, redelivery.Value);
        }

        await using var verify = fixture.CreateDbContext();
        var conversation = await verify.Conversations
            .Include("_messages")
            .SingleAsync(c => c.Id == conversationId, CancellationToken.None);

        var systemMessages = conversation.Messages.Where(m => m.AuthorKind == MessageAuthorKind.System).ToList();
        // The whole point: the second delivery staged an identical reply and the composite inbox key
        // threw all of it away, including the message row and its outbox row.
        var reply = Assert.Single(systemMessages);
        Assert.Equal(Fallback, reply.Body.Value);
        Assert.Equal(Guid.Empty, reply.AuthorId);

        var outboxRows = await verify.Set<OutboxMessage>()
            .Where(o => o.PartitionKey == conversationId.Value.ToString())
            .ToListAsync(CancellationToken.None);
        Assert.Single(outboxRows);
    }

    [Fact]
    public async Task WithAnOperatorOnline_NoReplyIsWritten()
    {
        var (siteId, operatorId, _) = await SeedSiteAsync(operatorStatus: OperatorStatus.Online);
        await EnableAutoReplyAsync(siteId, operatorId);
        var (conversationId, triggerMessageId, triggerSequence) = await SeedWaitingConversationAsync(siteId, "hello?");

        await using (var db = fixture.CreateDbContext())
        {
            var result = await CreateHandler(db, CreateCache()).HandleAsync(
                new SendOfflineAutoReply(triggerMessageId, siteId, conversationId, MessageAuthorKind.Visitor, triggerSequence),
                CancellationToken.None);

            Assert.Equal(OfflineAutoReplyOutcome.OperatorOnline, result.Value);
        }

        await using var verify = fixture.CreateDbContext();
        var conversation = await verify.Conversations
            .Include("_messages")
            .SingleAsync(c => c.Id == conversationId, CancellationToken.None);
        Assert.DoesNotContain(conversation.Messages, m => m.AuthorKind == MessageAuthorKind.System);
    }

    [Fact]
    public async Task TheScript_SurvivesARoundTripThroughItsOwnColumn()
    {
        var (siteId, operatorId, _) = await SeedSiteAsync(operatorStatus: OperatorStatus.Offline);

        await using (var db = fixture.CreateDbContext())
        {
            var result = await CreateUpdateHandler(db).HandleAsync(
                new UpdateOfflineAutoReply(
                    siteId, operatorId, Enabled: true, Fallback,
                    [
                        new UpdateOfflineAutoReplyRule("refund", "Refunds take three working days."),
                        new UpdateOfflineAutoReplyRule("delivery", "Delivery is two working days."),
                    ]),
                CancellationToken.None);
            Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : null);
        }

        await using var verify = fixture.CreateDbContext();
        var site = await verify.Sites.SingleAsync(s => s.Id == siteId, CancellationToken.None);

        Assert.True(site.OfflineAutoReply.Enabled);
        Assert.Equal(Fallback, site.OfflineAutoReply.FallbackReply);
        Assert.Equal(2, site.OfflineAutoReply.Rules.Count);
        // Order is part of the contract - first match wins, so a round trip that reordered the list
        // would silently change which reply a visitor gets.
        Assert.Equal("refund", site.OfflineAutoReply.Rules[0].Keyword);
        Assert.Equal("Delivery is two working days.", site.OfflineAutoReply.Rules[1].Reply);
    }

    /// <summary>
    /// The item's third Done-when, end to end: enabling the toggle from the console changes live
    /// behaviour rather than waiting out a TTL. Also the regression test for `14-04`'s own fix -
    /// before it, <c>SiteCacheInvalidationConsumer</c> evicted only <c>SiteCacheKeys.ForPublicKey</c>,
    /// so this id-keyed entry (the one the auto-reply consumer reads) would have kept saying "off"
    /// for up to five minutes after the operator switched it on.
    /// </summary>
    [Fact]
    public async Task EnablingTheToggle_InvalidatesTheIdKeyedCacheEntry_SoTheNextReadSeesItOn()
    {
        var (siteId, operatorId, _) = await SeedSiteAsync(operatorStatus: OperatorStatus.Offline);
        var cache = CreateCache();

        // Cold read, populating the id-keyed entry with "off" - exactly what the auto-reply consumer
        // would have read a moment before the operator flipped the switch.
        await using (var readDb = fixture.CreateDbContext())
        {
            var getSite = new GetSiteConfigByIdHandler(new SiteRepository(readDb), cache);
            var first = await getSite.HandleAsync(new GetSiteConfigById(siteId), CancellationToken.None);
            Assert.NotNull(first);
            Assert.False(first.OfflineAutoReply.Enabled);
        }

        await using var dispatcherConnection = fixture.CreateRabbitMqConnection();
        var dispatcher = new OutboxDispatcher(
            fixture.DataSource, new RabbitMqEventPublisher(dispatcherConnection, NullLogger<RabbitMqEventPublisher>.Instance), new SystemClock(),
            Options.Create(new OutboxDispatcherOptions { PollInterval = TimeSpan.FromMilliseconds(500) }),
            NullLogger<OutboxDispatcher>.Instance);

        await using var siteCacheConsumerConnection = fixture.CreateRabbitMqConnection();
        await using var siteCachePublisherConnection = fixture.CreateRabbitMqConnection();
        var siteCacheInvalidationConsumer = new SiteCacheInvalidationConsumer(
            new RabbitMqEventConsumer(siteCacheConsumerConnection),
            new CacheInvalidationPublisher(new RabbitMqEventPublisher(siteCachePublisherConnection, NullLogger<RabbitMqEventPublisher>.Instance), new SystemClock()),
            Options.Create(new SiteCacheInvalidationConsumerOptions()), NullLogger<SiteCacheInvalidationConsumer>.Instance);

        await using var cacheInvalidationConsumerConnection = fixture.CreateRabbitMqConnection();
        var cacheInvalidationConsumer = new CacheInvalidationConsumer(
            new RabbitMqEventConsumer(cacheInvalidationConsumerConnection), cache, NullLogger<CacheInvalidationConsumer>.Instance);

        // `15-17`: cacheInvalidationConsumer subscribes Broadcast, not Competing - its queue is
        // exclusive, auto-delete, and named with a random suffix generated inside SubscribeAsync
        // (RabbitMqSubscriptionTestHelpers' own remarks), so there is no name to passively declare the
        // way the Competing wait below uses. The only externally-observable fact is "how many queues
        // are now bound to this topic's exchange" - captured *before* starting the consumer, so an
        // unrelated queue left bound by an earlier test in this same collection fixture does not make
        // the wait below pass before this test's own subscription has actually landed.
        using var management = fixture.CreateRabbitMqManagementClient();
        var cacheInvalidatedBindingsBeforeStart = await RabbitMqSubscriptionTestHelpers.CountQueuesBoundToExchangeAsync(
            management, CacheTopics.Invalidated, CancellationToken.None);

        await dispatcher.StartAsync(CancellationToken.None);
        await siteCacheInvalidationConsumer.StartAsync(CancellationToken.None);
        await cacheInvalidationConsumer.StartAsync(CancellationToken.None);

        // `15-17`: wait for the fact each subscription's own queue (or, for the Broadcast one, a new
        // binding) actually exists, not a fixed sleep - see WebhookDispatchSharedQueueRegressionTests'
        // own remarks for why StartAsync alone cannot be awaited for this.
        await using var subscriptionProbeConnection = fixture.CreateRabbitMqConnection();
        await RabbitMqSubscriptionTestHelpers.AwaitAllCompetingSubscriptionsAsync(
            subscriptionProbeConnection, TimeSpan.FromSeconds(10),
            (nameof(SiteSettingsChanged), SiteCacheInvalidationConsumer.ConsumerName));
        var cacheInvalidationLanded = await RabbitMqSubscriptionTestHelpers.WaitForNewBroadcastSubscriptionAsync(
            management, CacheTopics.Invalidated, cacheInvalidatedBindingsBeforeStart, TimeSpan.FromSeconds(10));
        Assert.True(cacheInvalidationLanded,
            $"The Broadcast cache-invalidation subscription to '{CacheTopics.Invalidated}' never landed - no new " +
            "queue was bound to its exchange within 10s.");

        try
        {
            await EnableAutoReplyAsync(siteId, operatorId);

            var sawItOn = await OutboxTestHelpers.WaitUntilAsync(async () =>
            {
                await using var readDb = fixture.CreateDbContext();
                var getSite = new GetSiteConfigByIdHandler(new SiteRepository(readDb), cache);
                var read = await getSite.HandleAsync(new GetSiteConfigById(siteId), CancellationToken.None);
                return read is not null && read.OfflineAutoReply.Enabled;
            }, TimeSpan.FromSeconds(15));

            Assert.True(sawItOn, "Timed out waiting for the id-keyed site-config entry to reflect the enabled toggle.");
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
            await siteCacheInvalidationConsumer.StopAsync(CancellationToken.None);
            await cacheInvalidationConsumer.StopAsync(CancellationToken.None);
        }
    }

    private SendOfflineAutoReplyHandler CreateHandler(AgoChatDbContext db, RedisCache cache) =>
        new(new GetSiteConfigByIdHandler(new SiteRepository(db), cache),
            new ConversationRepository(db),
            new OperatorRepository(db),
            new EfOutboxWriter<AgoChatDbContext>(db),
            new EfInboxChecker<AgoChatDbContext>(db, new SystemClock()),
            new SystemClock(),
            new UuidV7Generator());

    private static UpdateOfflineAutoReplyHandler CreateUpdateHandler(AgoChatDbContext db) =>
        new(new SiteRepository(db), new PermissionChecker(db), new EfOutboxWriter<AgoChatDbContext>(db),
            new UuidV7Generator(), new SystemClock());

    private async Task EnableAutoReplyAsync(SiteId siteId, OperatorId operatorId)
    {
        await using var db = fixture.CreateDbContext();
        var result = await CreateUpdateHandler(db).HandleAsync(
            new UpdateOfflineAutoReply(siteId, operatorId, Enabled: true, Fallback, []), CancellationToken.None);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : null);
    }

    private async Task<(SiteId SiteId, OperatorId OperatorId, string PublicKey)> SeedSiteAsync(OperatorStatus operatorStatus)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var publicKey = $"site_{siteId.Value:N}";
        var roleId = Guid.NewGuid();

        await using var seed = fixture.CreateDbContext();
        seed.Sites.Add(new Site(siteId, publicKey, ["https://example.com"]));
        seed.Operators.Add(new Operator(operatorId, siteId, operatorStatus, capacity: 5));
        seed.Roles.Add(new RoleRecord
        {
            Id = roleId,
            SiteId = siteId,
            Name = "Admin",
            Permissions = [Permission.SiteConfigure.Value],
        });
        seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
        await seed.SaveChangesAsync(CancellationToken.None);

        return (siteId, operatorId, publicKey);
    }

    private async Task<(ConversationId ConversationId, Guid TriggerMessageId, int TriggerSequence)>
        SeedWaitingConversationAsync(SiteId siteId, string body)
    {
        var now = DateTimeOffset.UtcNow;
        var idGenerator = new UuidV7Generator();
        var visitorId = new VisitorId(idGenerator.NewId(now));
        var conversationId = new ConversationId(idGenerator.NewId(now));
        var messageId = new MessageId(idGenerator.NewId(now));

        await using var seed = fixture.CreateDbContext();
        seed.Visitors.Add(new Visitor(visitorId, siteId, now));
        var conversation = Conversation.Start(conversationId, siteId, visitorId, now);
        var message = conversation.AddVisitorMessage(visitorId, messageId, new MessageBody(body), now);
        conversation.ClearDomainEvents();
        seed.Conversations.Add(conversation);
        await seed.SaveChangesAsync(CancellationToken.None);

        return (conversationId, messageId.Value, message.Sequence);
    }
}
