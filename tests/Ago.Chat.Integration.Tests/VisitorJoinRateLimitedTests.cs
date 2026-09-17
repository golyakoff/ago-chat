using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Cors;
using Ago.Chat.Api.Hubs;
using Ago.Chat.Api.Realtime;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetConversationHistory;
using Ago.Chat.Application.UseCases.GetSiteConfigById;
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

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-107`: `VisitorHub.JoinCoreAsync` dereferenced `started.Value` without checking
/// `started.IsFailure` first - a rate-limited `StartConversationHandler.HandleAsync` call (the only
/// failure shape that handler has, both its per-visitor and per-site buckets returning
/// `ConversationErrors.ConversationCreateRateLimited`) threw `Result&lt;T&gt;.Value`'s own
/// `InvalidOperationException("Cannot access Value of a failed Result: ...")`, unhandled, surfacing to
/// the widget as a raw `500`/hub-invocation failure with no rate-limit information at all. Found live
/// running `capacity-ramp` (`load/reports/2026-09-15-local-capacity-ramp.md`).
///
/// <para>This exercises the real chain - real Postgres (<see cref="PostgresFixture"/>), the real,
/// unmodified <see cref="VisitorHub.JoinAsync"/> - through a <see cref="StartConversationHandler"/>
/// wired to a rate limiter that always denies, the same <see cref="RateLimitedFakeRateLimiter"/>
/// `VisitorSendIntoClosedConversationTests`'s own sibling file in this project already uses for the
/// identical shape on the send path.</para>
///
/// <para><b>Fails-before, actually run</b>: with <see cref="VisitorHub"/>'s `JoinCoreAsync` reverted to
/// its pre-`25-107` `var conversationId = started.Value.ConversationId;` (no `IsFailure` check), this
/// test's own <c>Assert.ThrowsAsync&lt;HubException&gt;</c> failed - the call threw
/// `InvalidOperationException`, a different type, not the `HubException` a caller can actually branch
/// on. Reverted after confirming the failure; never committed.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class VisitorJoinRateLimitedTests(PostgresFixture fixture)
{
    [Fact]
    public async Task JoinAsync_WhenConversationCreateIsRateLimited_ThrowsAHubExceptionNamingTheRealReason()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            await seed.SaveChangesAsync();
        }

        var hub = CreateHub(siteId, visitorId, "conn-1");

        var exception = await Assert.ThrowsAsync<HubException>(() => hub.JoinAsync());

        // Not the type this item found live (`InvalidOperationException`), and not empty - the real
        // message `ConversationErrors.ConversationCreateRateLimited` builds, naming the code and a
        // retry-after, matching how `SendAsync`'s own established `sent.IsFailure` branch (this file's
        // sibling test) already surfaces every other rate limit on this hub.
        Assert.Contains("Too many new conversations", exception.Message, StringComparison.Ordinal);
    }

    private VisitorHub CreateHub(SiteId siteId, VisitorId visitorId, string connectionId)
    {
        var db = fixture.CreateDbContext();
        var siteConfig = new GetSiteConfigByIdHandler(new SiteRepository(db), new NoOpCache());
        var startConversation = new StartConversationHandler(
            new VisitorRepository(db), new ConversationRepository(db), new VisitorRestrictionRepository(fixture.DataSource), siteConfig,
            new RateLimitedFakeRateLimiter(TimeSpan.FromSeconds(5.1)), new ConversationCreateRateLimitOptions(), new SystemClock(),
            new UuidV7Generator(), new VisitorEmojiPairGenerator());
        var getHistory = new GetConversationHistoryHandler(
            new ConversationRepository(db), new ConversationReadStore(fixture.DataSource), new PermissionChecker(db));
        var sendMessage = new SendVisitorMessageHandler(
            new ConversationRepository(fixture.CreateDbContext()), new FakeRateLimiter(), new MessageSendRateLimitOptions(),
            new SynchronousMessagePipeline(fixture.DataSource));
        var registration = new HubConnectionRegistration(
            new NoOpConnectionRegistry(), new LocalConnectionTracker(), new NodeId("test-node"));
        var originValidator = new HubOriginValidator(siteConfig);

        var hub = new VisitorHub(
            startConversation, sendMessage, getHistory, null!, registration, originValidator, new DrainState(),
            new NoOpWidgetActivityRecorder(), new SystemClock())
        {
            Context = new FakeHubCallerContext(connectionId, ClaimsPrincipalFor(siteId, visitorId)),
        };
        return hub;
    }

    private static ClaimsPrincipal ClaimsPrincipalFor(SiteId siteId, VisitorId visitorId) => new(new ClaimsIdentity(
    [
        new Claim(JwtRegisteredClaimNames.Sub, visitorId.Value.ToString()),
        new Claim(AgoClaimTypes.SiteId, siteId.Value.ToString()),
    ]));

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

    private sealed class NoOpWidgetActivityRecorder : IWidgetActivityRecorder
    {
        public void RecordLoad(SiteId siteId, DateTimeOffset now)
        {
        }

        public void RecordOpen(SiteId siteId, DateTimeOffset now)
        {
        }

        public void RecordConversation(SiteId siteId, DateTimeOffset now)
        {
        }
    }

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
