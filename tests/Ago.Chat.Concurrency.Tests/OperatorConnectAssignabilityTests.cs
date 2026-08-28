using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Ago.Chat.Api.Cors;
using Ago.Chat.Api.Hubs;
using Ago.Chat.Api.Realtime;
using Ago.Chat.Application.UseCases.AssignConversation;
using Ago.Chat.Application.UseCases.GetConversationHistory;
using Ago.Chat.Application.UseCases.GetVisitorHistory;
using Ago.Chat.Application.UseCases.GetVisitorPresence;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Application.UseCases.SetOperatorPresence;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Module;
using Ago.Chat.Worker;
using Ago.Platform.Abstractions;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Realtime;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>
/// `4-06`: the exact live symptom, reproduced and proven fixed against real Postgres and real Redis -
/// found live on 2026-08-27 by the author minting a demo tenant, sending a visitor message, and
/// watching it sit in the console's "Waiting" column forever. `MintDemoTenantHandler` and
/// `RegisterSiteHandler` both create their operator `Offline` (this test seeds the same way, not
/// `Online` as `NodeDeathReconnectTests`/`ConversationAssignmentConcurrencyTests` do - see each
/// other file's own seeding for the contrast, which is exactly why this bug was never caught before).
/// This proves the whole chain end to end: connect flips the DB row, the real claimer then finds it,
/// disconnect flips it back.
/// </summary>
[Collection(SiteCachingConcurrencyCollection.Name)]
public sealed class OperatorConnectAssignabilityTests(SiteCachingConcurrencyFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AnOperatorBornOffline_BecomesAssignable_OnConnect_AndUnassignable_OnLastDisconnect()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            // Offline, exactly like MintDemoTenantHandler/RegisterSiteHandler - the whole point of
            // this test is that nothing here hand-seeds Online the way most fixtures in this project do.
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Offline, capacity: 5));
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
            await db.SaveChangesAsync();
        }

        var claimer = new SkipLockedAssignmentClaimer(fixture.DataSource, new SystemClock(), new UuidV7Generator());

        // --- Before connecting: the exact bug. A real Waiting conversation, a real operator for the
        // site, and the claimer finds nobody, because Status is still Offline. ---
        var claimedBeforeConnect = await claimer.AssignWaitingConversationsAsync(siteId, batchSize: 10, CancellationToken.None);
        Assert.Equal(0, claimedBeforeConnect);

        // --- The operator connects, exactly as the console does on session start. ---
        var registry = new RedisConnectionRegistry(
            fixture.RedisMultiplexer, Options.Create(new ConnectionRegistryOptions()), NullLogger<RedisConnectionRegistry>.Instance);
        var tracker = new LocalConnectionTracker();
        var operatorHub = CreateOperatorHub(siteId, operatorId, "operator-conn-1", registry, tracker, new NodeId($"node-{Guid.NewGuid():N}"));
        await operatorHub.OnConnectedAsync();

        await using (var afterConnect = fixture.CreateDbContext())
        {
            var status = await afterConnect.Operators.AsNoTracking()
                .Where(o => o.Id == operatorId).Select(o => o.Status).SingleAsync();
            Assert.Equal(OperatorStatus.Online, status);
        }

        // --- The same claimer, same conversation, no other change - now it is claimed. ---
        var claimedAfterConnect = await claimer.AssignWaitingConversationsAsync(siteId, batchSize: 10, CancellationToken.None);
        Assert.Equal(1, claimedAfterConnect);

        await using (var afterClaim = fixture.CreateDbContext())
        {
            var conversation = await afterClaim.Conversations.AsNoTracking().SingleAsync(c => c.Id == conversationId);
            Assert.Equal(ConversationState.Assigned, conversation.State);
            Assert.Equal(operatorId, conversation.OperatorId);
        }

        // --- The operator's last connection drops - immediately excluded again, not left assignable
        // for new conversations while genuinely gone (Operator.GoOffline's own remarks). ---
        await operatorHub.OnDisconnectedAsync(exception: null);

        await using var afterDisconnect = fixture.CreateDbContext();
        var finalStatus = await afterDisconnect.Operators.AsNoTracking()
            .Where(o => o.Id == operatorId).Select(o => o.Status).SingleAsync();
        Assert.Equal(OperatorStatus.Offline, finalStatus);
    }

    /// <summary>
    /// `4-06`'s multi-connection case, missing from the fix's own test even though the production
    /// code already handles it: `OperatorHub.OnDisconnectedAsync` only calls `Operator.GoOffline` when
    /// `HubConnectionRegistration.OnDisconnectedAsync` reports `lastConnectionGone`, exactly mirroring
    /// 4-04's own multi-connection contract for the disconnect grace period. Two hubs, two
    /// `LocalConnectionTracker`s (one per connection, standing in for two Api replicas, or simply two
    /// browser tabs on the same one) sharing the one real Redis registry - the registry, not either
    /// tracker, is what makes "does the operator have connections left anywhere" the right question.
    /// </summary>
    [Fact]
    public async Task ASecondConnection_ThenDroppingOnlyOne_DoesNotFlipOffline()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Offline, capacity: 5));
            await db.SaveChangesAsync();
        }

        var registry = new RedisConnectionRegistry(
            fixture.RedisMultiplexer, Options.Create(new ConnectionRegistryOptions()), NullLogger<RedisConnectionRegistry>.Instance);

        // --- First connection: a tab opens. ---
        var operatorOnConnA = CreateOperatorHub(
            siteId, operatorId, "operator-conn-a", registry, new LocalConnectionTracker(), new NodeId($"node-{Guid.NewGuid():N}"));
        await operatorOnConnA.OnConnectedAsync();

        // --- Second connection, same operator: another tab, or another device. GoOnline is
        // idempotent (OperatorHub.OnConnectedAsync's own comment), so this must not disturb anything,
        // but the registry must now hold two live entries for this principal. ---
        var operatorOnConnB = CreateOperatorHub(
            siteId, operatorId, "operator-conn-b", registry, new LocalConnectionTracker(), new NodeId($"node-{Guid.NewGuid():N}"));
        await operatorOnConnB.OnConnectedAsync();

        await using (var afterBothConnect = fixture.CreateDbContext())
        {
            var status = await afterBothConnect.Operators.AsNoTracking()
                .Where(o => o.Id == operatorId).Select(o => o.Status).SingleAsync();
            Assert.Equal(OperatorStatus.Online, status);
        }

        // --- Drop only the first connection. The second is still live in the registry, so
        // lastConnectionGone must be false and Status must stay Online - the exact case that was
        // untested even though 4-06 already relies on it. ---
        await operatorOnConnA.OnDisconnectedAsync(exception: null);

        await using (var afterOneDrops = fixture.CreateDbContext())
        {
            var status = await afterOneDrops.Operators.AsNoTracking()
                .Where(o => o.Id == operatorId).Select(o => o.Status).SingleAsync();
            Assert.Equal(OperatorStatus.Online, status);
        }

        // --- Drop the second, and now last, connection - this is the one that must flip it. ---
        await operatorOnConnB.OnDisconnectedAsync(exception: null);

        await using var afterBothDrop = fixture.CreateDbContext();
        var finalStatus = await afterBothDrop.Operators.AsNoTracking()
            .Where(o => o.Id == operatorId).Select(o => o.Status).SingleAsync();
        Assert.Equal(OperatorStatus.Offline, finalStatus);
    }

    private OperatorHub CreateOperatorHub(
        SiteId siteId, OperatorId operatorId, string connectionId, IConnectionRegistry registry, LocalConnectionTracker tracker, NodeId node)
    {
        var db = fixture.CreateDbContext();
        var assignConversation = new AssignConversationHandler(new ConversationRepository(db), new PermissionChecker(db), new SystemClock());
        var sendMessage = new SendOperatorMessageHandler(
            new PermissionChecker(db), new SynchronousMessagePipeline(fixture.DataSource));
        var getHistory = new GetConversationHistoryHandler(
            new ConversationRepository(db), new ConversationReadStore(fixture.DataSource), new PermissionChecker(db));
        var getVisitorHistory = new GetVisitorHistoryHandler(
            new ConversationRepository(db), new ConversationReadStore(fixture.DataSource),
            new ChannelIdentityRepository(db), new PermissionChecker(db));
        var getVisitorPresence = new GetVisitorPresenceHandler(new ConversationRepository(db), new PermissionChecker(db), registry);
        var registration = new HubConnectionRegistration(registry, tracker, node);
        var presencePublisher = new OperatorPresencePublisher(new NoOpEventPublisher(), new SystemClock(), new UuidV7Generator());
        var operatorPresence = new SetOperatorPresenceHandler(new OperatorRepository(db));
        var consoleOrigin = new ConsoleOriginValidator(
            new ConsoleOriginOptions { AllowedOrigins = ["https://console.test"] });

        return new OperatorHub(
            assignConversation, sendMessage, getHistory, getVisitorHistory, getVisitorPresence, registration, consoleOrigin,
            presencePublisher, operatorPresence, new DrainState())
        {
            Context = new FakeHubCallerContext(connectionId, OperatorPrincipal(siteId, operatorId)),
            Clients = new FakeHubCallerClients(),
        };
    }

    private static ClaimsPrincipal OperatorPrincipal(SiteId siteId, OperatorId operatorId) => new(new ClaimsIdentity(
    [
        new Claim(JwtRegisteredClaimNames.Sub, operatorId.Value.ToString()),
        new Claim("site_id", siteId.Value.ToString()),
        new Claim("operator_id", operatorId.Value.ToString()),
    ]));

    private sealed class NoOpEventPublisher : IEventPublisher
    {
        public Task PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoOpClientProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeHubCallerClients : IHubCallerClients
    {
        private readonly NoOpClientProxy _proxy = new();
        public IClientProxy Caller => _proxy;
        public IClientProxy Others => _proxy;
        public IClientProxy All => _proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => _proxy;
        public IClientProxy Client(string connectionId) => _proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => _proxy;
        public IClientProxy Group(string groupName) => _proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => _proxy;
        public IClientProxy OthersInGroup(string groupName) => _proxy;
        public IClientProxy User(string userId) => _proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => _proxy;
    }

    private sealed class FakeHubCallerContext(string connectionId, ClaimsPrincipal user) : HubCallerContext
    {
        public override string ConnectionId { get; } = connectionId;

        public override string? UserIdentifier => null;

        public override ClaimsPrincipal? User { get; } = user;

        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

        public override IFeatureCollection Features { get; } = new FeatureCollection();

        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort()
        {
        }
    }
}
