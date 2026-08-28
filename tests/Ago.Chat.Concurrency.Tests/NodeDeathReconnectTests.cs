using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Ago.Chat.Api.Cors;
using Ago.Chat.Api.Hubs;
using Ago.Chat.Api.Realtime;
using Ago.Chat.Application.Realtime;
using Ago.Chat.Application.UseCases.AssignConversation;
using Ago.Chat.Application.UseCases.GetConversationHistory;
using Ago.Chat.Application.UseCases.GetSiteConfigById;
using Ago.Chat.Application.UseCases.GetVisitorHistory;
using Ago.Chat.Application.UseCases.GetVisitorPresence;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Application.UseCases.SetOperatorPresence;
using Ago.Chat.Application.UseCases.StartConversation;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Module;
using Ago.Platform.Abstractions;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Ago.Platform.Realtime;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>
/// `3-06`'s Done-when, the direct proof behind Stage 3's "three `Api` replicas serve one
/// conversation correctly": a visitor on node A, an operator on node B - node A dies (a real,
/// graceful `ConnectionDrainCoordinator` run, not a bare kill), the visitor reconnects to node C and
/// resumes correctly via the real registry, while node B's operator was never affected. Real
/// Postgres and real Redis (`SiteCachingConcurrencyFixture` - Postgres + Redis is exactly what this
/// needs, no RabbitMQ: the proof is about reconnect/resume through history, not live fan-out, which
/// 3-02's own tests already cover separately).
/// </summary>
[Collection(SiteCachingConcurrencyCollection.Name)]
public sealed class NodeDeathReconnectTests(SiteCachingConcurrencyFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task VisitorsNodeDies_TheyReconnectToADifferentNode_AndResumeCorrectly_WhileTheOperatorsNodeIsUnaffected()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var roleId = Guid.NewGuid();
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
            db.Roles.Add(new RoleRecord { Id = roleId, SiteId = siteId, Name = "Operator", Permissions = [Permission.ConversationAssign.Value, Permission.ConversationRead.Value, Permission.ConversationSend.Value] });
            db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
            await db.SaveChangesAsync();
        }

        var registry = new RedisConnectionRegistry(
            fixture.RedisMultiplexer, Options.Create(new ConnectionRegistryOptions()), NullLogger<RedisConnectionRegistry>.Instance);
        var nodeA = new NodeId($"node-a-{Guid.NewGuid():N}");
        var nodeB = new NodeId($"node-b-{Guid.NewGuid():N}");
        var nodeC = new NodeId($"node-c-{Guid.NewGuid():N}");
        var trackerA = new LocalConnectionTracker();
        var trackerB = new LocalConnectionTracker();
        var trackerC = new LocalConnectionTracker();

        // --- Node A: the visitor connects and starts the conversation. ---
        var visitorOnA = CreateVisitorHub(siteId, visitorId, "visitor-conn-a", registry, trackerA, nodeA);
        await visitorOnA.OnConnectedAsync();
        var joined = await visitorOnA.JoinAsync();
        var conversationId = new ConversationId(joined.ConversationId);
        Assert.Single(trackerA.Snapshot());

        // --- Node B: the operator connects and assigns the conversation - unaffected throughout. ---
        var operatorOnB = CreateOperatorHub(siteId, operatorId, "operator-conn-b", registry, trackerB, nodeB);
        await operatorOnB.OnConnectedAsync();
        await operatorOnB.JoinConversationAsync(conversationId.Value);
        await operatorOnB.SendMessageAsync(conversationId.Value, "hello from node B");

        // --- Node A dies: a real, graceful drain, not a bare kill. ---
        var dispatcherA = new NoOpLocalConnectionDispatcher();
        var coordinatorA = new ConnectionDrainCoordinator(
            trackerA, dispatcherA, registry, nodeA, new FakeHostApplicationLifetime(), new DrainState(),
            Options.Create(new DrainOptions { DrainTimeout = TimeSpan.FromSeconds(2) }), NullLogger<ConnectionDrainCoordinator>.Instance);
        await coordinatorA.StartAsync(CancellationToken.None);
        // The coordinator's own bounded wait is what actually clears trackerA (no client to remove it
        // for real in this test) - it gives up after DrainTimeout with the entry still present,
        // which is fine: RemoveNodeAsync's registry cleanup already happened by then regardless.
        await coordinatorA.StopAsync(CancellationToken.None);

        // Node A's registry entries are gone - the direct proof "the node died" actually did
        // something, not just that reconnect happens to work anyway.
        var afterDeath = await registry.GetConnectionsAsync(PrincipalKeys.ForVisitor(visitorId), CancellationToken.None);
        Assert.Empty(afterDeath);

        // --- The visitor reconnects - lands on node C, a fresh hub instance and a fresh registry
        // registration, exactly as SignalR's own model and 3-01's "any replica may accept any
        // connection" describe. ---
        var visitorOnC = CreateVisitorHub(siteId, visitorId, "visitor-conn-c", registry, trackerC, nodeC);
        await visitorOnC.OnConnectedAsync();
        var resumed = await visitorOnC.JoinAsync(lastKnownSequence: 0);

        Assert.False(resumed.IsNew);
        Assert.Equal(conversationId.Value, resumed.ConversationId);
        var message = Assert.Single(resumed.History);
        Assert.Equal("hello from node B", message.Body);
        Assert.Single(trackerC.Snapshot()); // registered under the new node

        var onlyNodeC = await registry.GetConnectionsAsync(PrincipalKeys.ForVisitor(visitorId), CancellationToken.None);
        var connection = Assert.Single(onlyNodeC);
        Assert.Equal(nodeC, connection.NodeId);

        // --- Node B (the operator) was never touched by any of this. ---
        var operatorStillThere = await registry.GetConnectionsAsync(PrincipalKeys.ForOperator(operatorId), CancellationToken.None);
        var operatorConnection = Assert.Single(operatorStillThere);
        Assert.Equal(nodeB, operatorConnection.NodeId);
    }

    private VisitorHub CreateVisitorHub(
        SiteId siteId, VisitorId visitorId, string connectionId, IConnectionRegistry registry, LocalConnectionTracker tracker, NodeId node)
    {
        var db = fixture.CreateDbContext();
        var startConversation = new StartConversationHandler(
            new VisitorRepository(db), new ConversationRepository(db), new SystemClock(), new UuidV7Generator());
        var getHistory = new GetConversationHistoryHandler(
            new ConversationRepository(db), new ConversationReadStore(fixture.DataSource), new PermissionChecker(db));
        var registration = new HubConnectionRegistration(registry, tracker, node);
        // FakeHubCallerContext.Features is always empty, so HubOriginValidator's own Origin check
        // short-circuits before ever calling GetSiteConfigByIdHandler - real SiteRepository/no-op
        // cache is fine, never exercised (same reasoning as ReconnectResumeTests' own CreateHub).
        var originValidator = new HubOriginValidator(new GetSiteConfigByIdHandler(new SiteRepository(db), new NoOpCache()));

        return new VisitorHub(startConversation, null!, getHistory, registration, originValidator, new DrainState())
        {
            Context = new FakeHubCallerContext(connectionId, VisitorPrincipal(siteId, visitorId)),
            Clients = new FakeHubCallerClients(),
        };
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
        // `5-18`: the operator hub validates the *console's* origin, not the tenant's widget origins.
        // FakeHubCallerContext carries no HttpContext, so no Origin header is present and the check
        // short-circuits to allowed - exactly as it does for the dev harness and any non-browser client.
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

    private static ClaimsPrincipal VisitorPrincipal(SiteId siteId, VisitorId visitorId) => new(new ClaimsIdentity(
    [
        new Claim(JwtRegisteredClaimNames.Sub, visitorId.Value.ToString()),
        new Claim("site_id", siteId.Value.ToString()),
    ]));

    // `5-05`/`adr/0022`: `sub` is no longer this project's own OperatorId (it is Keycloak's own
    // subject once real tokens are validated) - constructing the hub directly, this test bypasses
    // OperatorIdentityClaimsTransformation entirely, so it adds the same "operator_id" claim that
    // transformation would have produced by hand, exactly like `site_id` above.
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

    private sealed class NoOpLocalConnectionDispatcher : ILocalConnectionDispatcher
    {
        public Task<DispatchOutcome> DispatchAsync(ConnectionId connectionId, string method, string payloadJson, CancellationToken cancellationToken) =>
            Task.FromResult(DispatchOutcome.Delivered);
    }

    private sealed class FakeHostApplicationLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    private sealed class NoOpClientProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    // The hub's own SendAsync echo (SendMessageAsync -> Clients.Caller.SendAsync) needs a non-null
    // Clients to run at all - a no-op proxy is enough since this test asserts through the registry
    // and history, not through what got "sent" over a client connection that doesn't exist here.
    private sealed class FakeHubCallerClients : IHubCallerClients
    {
        private static readonly IClientProxy Proxy = new NoOpClientProxy();

        public IClientProxy All => Proxy;

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;

        public IClientProxy Client(string connectionId) => Proxy;

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;

        public IClientProxy Group(string groupName) => Proxy;

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;

        public IClientProxy User(string userId) => Proxy;

        public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;

        public IClientProxy Caller => Proxy;

        public IClientProxy Others => Proxy;

        public IClientProxy OthersInGroup(string groupName) => Proxy;
    }

    /// <summary>Always misses - `caching.md`'s own documented Redis-failure behaviour, reused here
    /// since this file has no Redis fixture and `HubOriginValidator` never actually reaches it (see
    /// <see cref="CreateVisitorHub"/>/<see cref="CreateOperatorHub"/>'s own remarks).</summary>
    private sealed class NoOpCache : ICache
    {
        public Task<T?> GetAsync<T>(CacheKey key, CancellationToken cancellationToken) where T : class => Task.FromResult<T?>(default);

        public Task SetAsync<T>(CacheKey key, T value, CacheEntryOptions options, CancellationToken cancellationToken) where T : class =>
            Task.CompletedTask;

        public Task<T> GetOrCreateAsync<T>(
            CacheKey key, Func<CancellationToken, Task<T>> factory, CacheEntryOptions options, CancellationToken cancellationToken) where T : class =>
            factory(cancellationToken);

        public Task RemoveAsync(CacheKey key, CancellationToken cancellationToken) => Task.CompletedTask;
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
