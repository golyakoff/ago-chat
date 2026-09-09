using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Cors;
using Ago.Chat.Api.Hubs;
using Ago.Chat.Api.Realtime;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.AssignConversation;
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
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-53`: "the visitor sees an empty thread while the operator sees the whole conversation". The
/// actual defect lived in <c>ago-widget</c> - <c>VisitorConnection.start()</c> sent the browser's
/// stored <c>lastKnownSequence</c> cursor on the very first join of a fresh page load, so a visitor
/// who had already read everything before closing the tab got back an empty *delta* (nothing changed
/// since a cursor that already sat at the newest sequence there was) into a DOM that had never
/// rendered a single bubble. <see cref="ReconnectResumeTests.JoinAsync_WithLastKnownSequence_WhenNothingWasMissed_ReturnsAnEmptyHistory"/>
/// already proves that answer is *correct* for a genuine reconnect, where the caller's own DOM
/// already holds everything up to that cursor - the bug was the widget reusing that same call for a
/// situation where it does not.
///
/// <para>Nothing here changes: <c>VisitorHub.JoinAsync()</c> with no <c>lastKnownSequence</c> already
/// answered with the visitor's own most recent history page before this item, and still does - this
/// file exists to lock that contract down explicitly, from the exact hub method the fixed widget now
/// actually calls on every fresh page load, and to prove the two things `23-53`'s own backlog item
/// says the implementation must settle: an operator note and a real system event (an assignment) in
/// the same conversation never reach this read, and neither does another visitor's or another site's
/// conversation.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class VisitorReopenHistoryTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    /// <summary>
    /// The regression this item exists to close, at the server boundary the widget now actually
    /// relies on. This exercises code that did not change - <c>GetConversationHistoryHandler</c> and
    /// <c>ConversationReadStore</c> were already correct - so it is a contract lock, not a fails-before
    /// proof of a server-side defect: the defect was entirely in <c>ago-widget</c>'s own choice of
    /// which argument to send (see <c>ago-widget</c>'s own `connection.test.ts`, whose fails-before
    /// table is for that repository's own fix).
    /// </summary>
    [Fact]
    public async Task JoinAsync_WithNoLastKnownSequence_OnAConversationTheVisitorAlreadyHasMessagesIn_ReturnsTheFullHistory()
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
        var conversationId = new ConversationId(joined.ConversationId);

        await using (var writeDb = fixture.CreateDbContext())
        {
            var sendMessage = new SendVisitorMessageHandler(
                new ConversationRepository(writeDb), new FakeRateLimiter(), new MessageSendRateLimitOptions(),
                new SynchronousMessagePipeline(fixture.DataSource));
            for (var i = 1; i <= 3; i++)
            {
                var sent = await sendMessage.HandleAsync(
                    new SendVisitorMessage(conversationId, visitorId, $"message {i}"), CancellationToken.None);
                Assert.True(sent.IsSuccess);
            }
        }

        // The visitor closes the tab having read everything - the browser's own stored cursor, were
        // this driven through the real widget, would now sit at the newest sequence there is. A fresh
        // page load carries no cursor into this call at all (the fixed widget's own `undefined`) - a
        // brand new hub instance, exactly as SignalR itself builds one per connection.
        var reopened = await CreateHub(siteId, visitorId, "conn-2").JoinAsync();

        Assert.False(reopened.IsNew);
        Assert.Equal(conversationId.Value, reopened.ConversationId);
        // `IConversationReadStore.GetHistoryAsync`'s own contract for `beforeSequence: null`: the most
        // recent page, newest first - the same keyset-pagination direction "load older messages" would
        // continue in. `ago-widget`'s own fix reverses this for display (its `ui/widget.ts` doc comment
        // explains why); this hub-level test asserts the wire contract exactly as it is, not as a
        // particular client chooses to render it.
        Assert.Equal(["message 3", "message 2", "message 1"], reopened.History.Select(m => m.Body));
    }

    /// <summary>
    /// `23-53`'s own Scope: "the read must return what was addressed to the visitor, not everything
    /// the conversation contains... asserted by a test that puts an operator note and a system event
    /// in the same conversation and proves neither comes back." An assignment
    /// (<see cref="Conversation.AssignTo"/>, raising the real <see cref="ConversationAssigned"/>
    /// domain event) is this codebase's own concrete "system event" tied to a conversation - it is
    /// what actually happens the moment "the operator answered" in `23-53`'s own bug report, and,
    /// like a <see cref="ConversationNote"/>, it carries the operator's identity into the conversation
    /// without being a message the visitor ever wrote or was ever addressed to them.
    ///
    /// <para><b>Fails-before, actually run</b> (this item's own requirement, the same technique
    /// `NoteLeakProofTests` used first): with a note stored in its own `conversation_notes` table,
    /// reached only through <see cref="NoteRepository"/>, there is no code path from
    /// <see cref="ConversationReadStore.GetHistoryAsync"/> to a note at all - the query cannot leak
    /// one by construction. Proving this test can catch the defect class needed the defect actually
    /// present once: <c>ConversationReadStore.GetHistoryAsync</c>'s <c>Sql</c> constant was temporarily
    /// changed to <c>union all</c> a second branch selecting from <c>conversation_notes</c>, reshaped
    /// to look like a `messages` row (the identical mutation <c>NoteLeakProofTests</c> already
    /// documents), and this test re-run against the real container. It failed exactly where a real
    /// leak would surface - the note's own body, verbatim, in the page the visitor was about to
    /// receive. The mutation was reverted immediately after, never committed; see this item's own
    /// commit-prep notes for the exact diff exercised.</para>
    /// </summary>
    [Fact]
    public async Task JoinAsync_ConversationCarriesANoteAndAnAssignmentEvent_NeitherReachesTheVisitor()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            seed.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
            await seed.SaveChangesAsync();
        }

        var joined = await CreateHub(siteId, visitorId, "conn-1").JoinAsync();
        var conversationId = new ConversationId(joined.ConversationId);

        await using (var writeDb = fixture.CreateDbContext())
        {
            var sendMessage = new SendVisitorMessageHandler(
                new ConversationRepository(writeDb), new FakeRateLimiter(), new MessageSendRateLimitOptions(),
                new SynchronousMessagePipeline(fixture.DataSource));
            var sent = await sendMessage.HandleAsync(
                new SendVisitorMessage(conversationId, visitorId, "Hi, I need help with my order."), CancellationToken.None);
            Assert.True(sent.IsSuccess);
        }

        // The real system event: an assignment, raised by the same Conversation.AssignTo the
        // automatic engine and the console's own claim path both use. Asserted to have actually
        // happened (a domain event genuinely on this conversation), not merely assumed.
        ConversationAssigned? assignedEvent;
        await using (var writeDb = fixture.CreateDbContext())
        {
            var conversations = new ConversationRepository(writeDb);
            var conversation = await conversations.GetByIdAsync(conversationId, CancellationToken.None);
            conversation!.AssignTo(operatorId, Now);
            assignedEvent = conversation.DomainEvents.OfType<ConversationAssigned>().SingleOrDefault();
            await conversations.SaveAsync(conversation, CancellationToken.None);
        }

        Assert.NotNull(assignedEvent);
        Assert.Equal(operatorId, assignedEvent!.OperatorId);

        const string operatorReplyBody = "One moment, let me check that order for you.";
        await using (var writeDb = fixture.CreateDbContext())
        {
            var conversations = new ConversationRepository(writeDb);
            var conversation = await conversations.GetByIdAsync(conversationId, CancellationToken.None);
            conversation!.AddOperatorMessage(operatorId, new MessageId(Guid.NewGuid()), new MessageBody(operatorReplyBody), Now);
            await conversations.SaveAsync(conversation, CancellationToken.None);
        }

        // A note only an operator should ever see - written so a leak is unmistakable in the
        // assertion output rather than a bland string a passing test could coincide with by luck
        // (NoteLeakProofTests' own reasoning for its choice of text).
        const string noteBody = "INTERNAL: this visitor threatened a chargeback, watch for repeat orders.";
        await using (var writeDb = fixture.CreateDbContext())
        {
            var note = ConversationNote.Write(
                new ConversationNoteId(Guid.NewGuid()), conversationId, operatorId, noteBody, Now);
            await new NoteRepository(writeDb).SaveAsync(note, CancellationToken.None);
        }

        // The visitor reopens - a fresh hub instance, no lastKnownSequence, exactly the call the fixed
        // widget makes on every plain page load.
        var reopened = await CreateHub(siteId, visitorId, "conn-2").JoinAsync();

        Assert.False(reopened.IsNew);
        // The two real messages are there - a leak-proof test that returned nothing would prove
        // nothing about leakage, only that the query is broken (NoteLeakProofTests' own remarks).
        Assert.Equal(2, reopened.History.Count);
        Assert.Contains(reopened.History, m => m.Body == "Hi, I need help with my order.");
        Assert.Contains(reopened.History, m => m.Body == operatorReplyBody);

        // The actual guarantee: nothing the visitor received carries the note's text or its author,
        // and nothing beyond the operator's own ordinary reply carries the operator's identity at
        // all - there is no third item, no assignment metadata, no trace of the ConversationAssigned
        // event itself riding along on any message.
        Assert.DoesNotContain(reopened.History, m => m.Body.Contains("chargeback", StringComparison.Ordinal));
        Assert.DoesNotContain(reopened.History, m => m.Body.Contains("INTERNAL", StringComparison.Ordinal));
        Assert.All(reopened.History, m => Assert.True(m.AuthorId == visitorId.Value || m.AuthorId == operatorId.Value));
    }

    /// <summary>`23-53`'s own Scope, "a visitor cannot read another visitor's conversation" -
    /// the same-site half. <see cref="CrossTenantConversationAccessTests.AVisitorOfAnotherSite_CannotReadTheConversation"/>
    /// already proves the cross-site half through <c>GetConversationHistoryHandler</c> directly; this
    /// is the same guarantee through the real hub, for a visitor of the *same* site as the
    /// conversation's own visitor - the case a site-only comparison could not catch, since both
    /// visitors here share one <see cref="SiteId"/> and the only thing standing between them is the
    /// per-conversation <see cref="Conversation.VisitorId"/> check.</summary>
    [Fact]
    public async Task JoinAsync_TwoVisitorsOfTheSameSite_NeitherReadsTheOthersConversation()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var firstVisitorId = new VisitorId(Guid.NewGuid());
        var secondVisitorId = new VisitorId(Guid.NewGuid());
        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            await seed.SaveChangesAsync();
        }

        var firstJoin = await CreateHub(siteId, firstVisitorId, "conn-1").JoinAsync();
        var firstConversationId = new ConversationId(firstJoin.ConversationId);
        await using (var writeDb = fixture.CreateDbContext())
        {
            var sendMessage = new SendVisitorMessageHandler(
                new ConversationRepository(writeDb), new FakeRateLimiter(), new MessageSendRateLimitOptions(),
                new SynchronousMessagePipeline(fixture.DataSource));
            var sent = await sendMessage.HandleAsync(
                new SendVisitorMessage(firstConversationId, firstVisitorId, "the first visitor's own message"), CancellationToken.None);
            Assert.True(sent.IsSuccess);
        }

        // The second visitor's own connection: their own conversation is created and joined
        // normally, and their own history is exactly their own - the identity check has to actually
        // be running, not merely absent an attack to trip it.
        var secondJoin = await CreateHub(siteId, secondVisitorId, "conn-2").JoinAsync();
        Assert.NotEqual(firstConversationId.Value, secondJoin.ConversationId);
        Assert.Empty(secondJoin.History);

        // The direct attempt: the second visitor's own token asking for the first visitor's
        // conversation id by number, through GetHistoryAsync (the same call `loadOlderHistory` makes).
        await Assert.ThrowsAsync<HubException>(() =>
            CreateHub(siteId, secondVisitorId, "conn-3").GetHistoryAsync(firstConversationId.Value, null, 50));
    }

    private VisitorHub CreateHub(SiteId siteId, VisitorId visitorId, string connectionId)
    {
        var db = fixture.CreateDbContext();
        // `23-78`: shared with originValidator right below - real SiteRepository/no-op cache, never
        // exercised for what this test actually checks.
        var siteConfig = new GetSiteConfigByIdHandler(new SiteRepository(db), new NoOpCache());
        var startConversation = new StartConversationHandler(
            new VisitorRepository(db), new ConversationRepository(db), siteConfig, new SystemClock(), new UuidV7Generator());
        var getHistory = new GetConversationHistoryHandler(
            new ConversationRepository(db), new ConversationReadStore(fixture.DataSource), new PermissionChecker(db));
        var registration = new HubConnectionRegistration(
            new NoOpConnectionRegistry(), new LocalConnectionTracker(), new NodeId("test-node"));
        var originValidator = new HubOriginValidator(siteConfig);

        var hub = new VisitorHub(
            startConversation, null!, getHistory, registration, originValidator, new DrainState(),
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
