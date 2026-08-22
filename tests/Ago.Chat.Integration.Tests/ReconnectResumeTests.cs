using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Ago.Chat.Api.Hubs;
using Ago.Chat.Api.Realtime;
using Ago.Chat.Application.UseCases.GetConversationHistory;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Application.UseCases.StartConversation;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Ago.Platform.Realtime;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `3-03`'s concrete proof: a fresh <see cref="VisitorHub"/> instance - SignalR's own model already
/// treats each connection as a new hub instance, so this is a faithful stand-in for "a fresh hub
/// connection" without standing up a real WebSocket transport (`HubConnectionRegistrationTests`' own
/// comment notes why 3-01 never needed a <see cref="HubCallerContext"/> fake; this is the slice where
/// one becomes necessary). Real Postgres; the connection registry is never exercised by
/// <c>JoinAsync</c> itself, so it is a no-op fake rather than a real Redis container - see
/// <see cref="NoOpConnectionRegistry"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ReconnectResumeTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    [Fact]
    public async Task JoinAsync_WithLastKnownSequence_ReturnsExactlyTheMissedMessages_NoGapNoDuplicateNoFullReplay()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            await seed.SaveChangesAsync();
        }

        var firstConnection = CreateHub(siteId, visitorId, "conn-1");
        var joined = await firstConnection.JoinAsync();
        Assert.True(joined.IsNew);
        var conversationId = new ConversationId(joined.ConversationId);

        await using (var writeDb = fixture.CreateDbContext())
        {
            var sendMessage = new SendVisitorMessageHandler(
                new ConversationRepository(writeDb), new SystemClock(), new UuidV7Generator(),
                new EfOutboxWriter<AgoChatDbContext>(writeDb), new FakeRateLimiter(), new MessageSendRateLimitOptions());
            for (var i = 1; i <= 5; i++)
            {
                var sent = await sendMessage.HandleAsync(
                    new SendVisitorMessage(conversationId, visitorId, $"message {i}"), CancellationToken.None);
                Assert.True(sent.IsSuccess);
            }
        }

        // A fresh instance - VisitorHub carries no state between calls, so this stands in for a
        // reconnect on a brand new connection exactly as SignalR itself would construct one.
        var reconnected = CreateHub(siteId, visitorId, "conn-2");
        var resumed = await reconnected.JoinAsync(lastKnownSequence: 3);

        Assert.False(resumed.IsNew);
        Assert.Equal(conversationId.Value, resumed.ConversationId);
        Assert.Equal([4, 5], resumed.History.Select(m => m.Sequence));
        Assert.Equal(["message 4", "message 5"], resumed.History.Select(m => m.Body));
    }

    [Fact]
    public async Task JoinAsync_WithLastKnownSequence_WhenNothingWasMissed_ReturnsAnEmptyHistory()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            await seed.SaveChangesAsync();
        }

        var joined = await CreateHub(siteId, visitorId, "conn-1").JoinAsync();

        var resumed = await CreateHub(siteId, visitorId, "conn-2").JoinAsync(lastKnownSequence: 0);

        Assert.False(resumed.IsNew);
        Assert.Equal(joined.ConversationId, resumed.ConversationId);
        Assert.Empty(resumed.History);
    }

    private VisitorHub CreateHub(SiteId siteId, VisitorId visitorId, string connectionId)
    {
        var db = fixture.CreateDbContext();
        var startConversation = new StartConversationHandler(
            new VisitorRepository(db), new ConversationRepository(db), new SystemClock(), new UuidV7Generator());
        var getHistory = new GetConversationHistoryHandler(
            new ConversationRepository(db), new ConversationReadStore(fixture.DataSource), new PermissionChecker(db));
        var registration = new HubConnectionRegistration(
            new NoOpConnectionRegistry(), new LocalConnectionTracker(), new NodeId("test-node"));

        // sendMessage is never used - only JoinAsync is exercised in this file, and messages are
        // seeded directly through SendVisitorMessageHandler in the test body instead of through
        // the hub's SendMessageAsync (which also echoes to Clients.Caller, irrelevant here).
        var hub = new VisitorHub(startConversation, null!, getHistory, registration, new DrainState())
        {
            Context = new FakeHubCallerContext(connectionId, ClaimsPrincipalFor(siteId, visitorId)),
        };
        return hub;
    }

    private static ClaimsPrincipal ClaimsPrincipalFor(SiteId siteId, VisitorId visitorId) => new(new ClaimsIdentity(
    [
        new Claim(JwtRegisteredClaimNames.Sub, visitorId.Value.ToString()),
        // "site_id" - Ago.Chat.Api.Auth.AgoClaimTypes.SiteId, internal to that assembly.
        new Claim("site_id", siteId.Value.ToString()),
    ]));

    /// <summary><c>JoinAsync</c> never calls <c>OnConnectedAsync</c>/<c>OnDisconnectedAsync</c> - a
    /// hand-built hub instance in a test has no SignalR pipeline invoking those lifecycle methods
    /// automatically - so the registry this only exists to satisfy <see cref="VisitorHub"/>'s
    /// constructor is never actually read from or written to.</summary>
    private sealed class NoOpConnectionRegistry : IConnectionRegistry
    {
        public Task RegisterAsync(ConnectionId connectionId, NodeId nodeId, PrincipalKey principal, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task UnregisterAsync(ConnectionId connectionId, NodeId nodeId, PrincipalKey principal, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyCollection<RegisteredConnection>> GetConnectionsAsync(PrincipalKey principal, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<RegisteredConnection>>([]);

        public Task RemoveNodeAsync(NodeId nodeId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Microsoft's own supported seam for unit-testing a <see cref="Hub"/> without a real
    /// connection - <c>Hub.Context</c> has a public setter for exactly this.</summary>
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
