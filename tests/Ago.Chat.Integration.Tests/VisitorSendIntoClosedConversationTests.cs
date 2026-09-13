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
/// `25-61`: "the widget never says the conversation already closed" - the reactive half of that
/// item's own two-repo gap. The investigation (backlog file, `2026-09-12`) traced the send path all
/// the way from `VisitorHub.SendAsync` through `SendVisitorMessageHandler`, the pipeline, and
/// `MessageBatchWriter`'s own catch of `InvalidConversationStateException`, down to
/// <see cref="Conversation.AddVisitorMessage"/>'s closed-conversation check - and found that
/// <c>Error.Code</c> ("Conversation.InvalidState") never survived the trip into the
/// <see cref="HubException"/> the widget actually sees, which carried the free-text message only.
///
/// <para>This exercises that whole real chain - real Postgres (<see cref="PostgresFixture"/>), the
/// real domain <see cref="Conversation.Close"/>, the real <see cref="SendVisitorMessageHandler"/> and
/// <see cref="SynchronousMessagePipeline"/> (itself the real <c>MessageBatchWriter</c>, batched as
/// one) - through the real, unmodified <see cref="VisitorHub.SendMessageAsync"/> - not a unit test
/// that stubs <see cref="SendVisitorMessageHandler"/> to hand back a canned <c>Error</c> without ever
/// running <see cref="Conversation.AddVisitorMessage"/>'s own check. A stub proves the hub's own
/// branch compiles; this proves the signal a real closed conversation produces actually reaches it.
/// </para>
///
/// <para><b>Fails-before, actually run</b>: with <see cref="VisitorHub.SendAsync"/>'s failure branch
/// reverted to its pre-`25-61` <c>throw new HubException(sent.Error!.Value.Message)</c>, the first
/// assertion below (the prefix) failed - the caught <see cref="HubException.Message"/> was the bare
/// English sentence <see cref="Conversation.AddVisitorMessage"/> throws, with no
/// <c>"Conversation.InvalidState: "</c> prefix for a caller to branch on. Reverted after confirming
/// the failure; never committed.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class VisitorSendIntoClosedConversationTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    [Fact]
    public async Task SendMessageAsync_IntoAConversationThatHasAlreadyClosed_ThrowsAHubExceptionCarryingTheInvalidStateCodeAsAPrefix()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            await seed.SaveChangesAsync();
        }

        var hub = CreateHub(siteId, visitorId, "conn-1");
        var joined = await hub.JoinAsync();
        var conversationId = new ConversationId(joined.ConversationId);

        await using (var writeDb = fixture.CreateDbContext())
        {
            var conversations = new ConversationRepository(writeDb);
            var conversation = await conversations.GetByIdAsync(conversationId, CancellationToken.None);
            conversation!.Close(Now);
            await conversations.SaveAsync(conversation, CancellationToken.None);
        }

        var exception = await Assert.ThrowsAsync<HubException>(
            () => hub.SendMessageAsync(conversationId.Value, "is anyone still there?", null, Guid.NewGuid()));

        // The actual wire contract `ago-widget`'s own `25-61` reads: a caller that only cares whether
        // this send failed *because the conversation is closed* checks this exact prefix (asserted
        // against the hub's own published constant, not a re-typed literal, so a future rename of the
        // constant fails this test rather than silently drifting from what the widget was told to
        // expect), never the English sentence after it - which may be reworded or localised without
        // notice, exactly the brittle match this item's own investigation warned against.
        Assert.StartsWith(VisitorHub.ConversationClosedHubErrorPrefix, exception.Message, StringComparison.Ordinal);
        // The original message still rides along, unmodified, after the prefix - nothing here erases
        // the human-readable detail that `Conversation.AddVisitorMessage` throws.
        Assert.Contains($"closed conversation {conversationId.Value}", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Counterpart to the fact above: a rejection that is *not* about a closed conversation
    /// (a participant mismatch, mapped to `Conversation.Forbidden`) must not pick up the same prefix -
    /// this item's own "don't collapse this into the same generic error path" warning, restated for
    /// the new branch itself rather than only for the old one.</summary>
    [Fact]
    public async Task SendMessageAsync_ByAVisitorWhoIsNotThisConversationsOwnVisitor_ThrowsAPlainHubExceptionWithNoPrefix()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var ownerVisitorId = new VisitorId(Guid.NewGuid());
        var otherVisitorId = new VisitorId(Guid.NewGuid());
        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            await seed.SaveChangesAsync();
        }

        var ownerJoin = await CreateHub(siteId, ownerVisitorId, "conn-1").JoinAsync();
        var conversationId = ownerJoin.ConversationId;

        var otherHub = CreateHub(siteId, otherVisitorId, "conn-2");
        var exception = await Assert.ThrowsAsync<HubException>(
            () => otherHub.SendMessageAsync(conversationId, "not my conversation", null, Guid.NewGuid()));

        Assert.False(exception.Message.StartsWith(VisitorHub.ConversationClosedHubErrorPrefix, StringComparison.Ordinal));
    }

    private VisitorHub CreateHub(SiteId siteId, VisitorId visitorId, string connectionId)
    {
        var db = fixture.CreateDbContext();
        var siteConfig = new GetSiteConfigByIdHandler(new SiteRepository(db), new NoOpCache());
        var startConversation = new StartConversationHandler(
            new VisitorRepository(db), new ConversationRepository(db), siteConfig,
            new FakeRateLimiter(), new ConversationCreateRateLimitOptions(), new SystemClock(), new UuidV7Generator(),
            new VisitorEmojiPairGenerator());
        var getHistory = new GetConversationHistoryHandler(
            new ConversationRepository(db), new ConversationReadStore(fixture.DataSource), new PermissionChecker(db));
        var sendMessage = new SendVisitorMessageHandler(
            new ConversationRepository(fixture.CreateDbContext()), new FakeRateLimiter(), new MessageSendRateLimitOptions(),
            new SynchronousMessagePipeline(fixture.DataSource));
        var registration = new HubConnectionRegistration(
            new NoOpConnectionRegistry(), new LocalConnectionTracker(), new NodeId("test-node"));
        var originValidator = new HubOriginValidator(siteConfig);

        var hub = new VisitorHub(
            startConversation, sendMessage, getHistory, registration, originValidator, new DrainState(),
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
