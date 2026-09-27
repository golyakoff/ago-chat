using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.AutoCloseConversation;
using Ago.Chat.Application.UseCases.GetSiteConfigById;
using Ago.Chat.Application.UseCases.ReceiveChannelMessage;
using Ago.Chat.Application.UseCases.ReleaseInactiveConversation;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Application.UseCases.StartConversation;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Worker;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `18-06`: real Postgres, the real domain path (`AutoCloseConversationHandler` ->
/// <c>Conversation.Close()</c> -> the outbox -> `6-09`'s capacity release), and the real reuse-or-new
/// logic `StartConversationHandler`/`ConversationRepository.GetActiveForVisitorAsync` already own -
/// the same bar `CloseConversationOutboxTests` and `OperatorConversationReleaserTests` already set for
/// the handlers this job builds on. This is a state change only: nothing here deletes or archives a
/// row - every assertion below reads the closed conversation's own row back, unchanged except for
/// `state`.
///
/// <para>`25-118`: the widget bucket is now two passes (release, then close) against two different
/// windows - see <see cref="RunOnceAsync_ReleasesAnAssignedWidgetConversationPastWidgetInactivityWindow_BackToWaiting_AndFreesCapacity"/>
/// and <see cref="RunOnceAsync_ClosesAWaitingWidgetConversationPastWidgetCloseWindow"/> for the two
/// load-bearing new behaviours, proven against real Postgres exactly as the pre-existing tests below
/// prove the original single-pass shape. At the time, the channel-kind tests further down were
/// untouched and still passed unmodified - proof that the channel-kind path was unaffected by that
/// item specifically.</para>
///
/// <para>`26-232` ends that channel-kind exemption: a `Waiting` channel-kind conversation is now closed
/// past its own per-`ChannelKind` window, the same way `25-118` already did for the widget bucket - see
/// <see cref="RunOnceAsync_ClosesAWaitingChannelKindConversationPastItsWindow"/> (the fails-before proof)
/// and <see cref="RunOnceAsync_LeavesAWaitingChannelKindConversationAlone_WhenNotYetPastItsWindow"/> (its
/// negative pairing). Unlike the widget bucket, this is one window widened to two states, not two
/// windows: <see cref="AutoCloseInactiveConversationsJobOptions.WindowFor"/> is unchanged, so the 24-hour
/// default still lives in exactly one place.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AutoCloseInactiveConversationsJobTests(PostgresFixture fixture)
{
    // Real time, not a fixed date - MessageUniqueSequenceTests' own remarks explain why: the
    // partitioned messages table only ever has partitions for the current month and the next two.
    // Truncated to whole seconds so it round-trips through timestamptz unchanged.
    private static readonly DateTimeOffset Now =
        new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    /// <summary>`25-118`'s own fails-before table, row 1: before this item, this exact scenario closed
    /// the conversation outright (this test used to assert `ConversationState.Closed` and a
    /// `ConversationEnded` outbox row - see git history for the pre-`25-118` version). Mutating the job
    /// back to that single-pass shape (or leaving `WidgetCloseWindow` defaulted to something shorter
    /// than this test's age) reproduces the old, now-wrong behaviour and this test fails; restoring the
    /// two-pass split makes it pass again. Proves: an `Assigned` widget conversation past
    /// `WidgetInactivityWindow` but nowhere near `WidgetCloseWindow` (defaulted to 7 days, left
    /// unconfigured here) is released back to `Waiting`, not closed, and its operator's capacity slot
    /// frees immediately regardless.</summary>
    [Fact]
    public async Task RunOnceAsync_ReleasesAnAssignedWidgetConversationPastWidgetInactivityWindow_BackToWaiting_AndFreesCapacity()
    {
        var widgetWindow = TimeSpan.FromHours(1);
        var seeded = await SeedAssignedConversationAsync(
            createdAt: Now - widgetWindow - TimeSpan.FromMinutes(1), holdsCapacityClaim: true);

        await CreateJob(new AutoCloseInactiveConversationsJobOptions
        {
            WidgetInactivityWindow = widgetWindow,
            // WidgetCloseWindow left at its 7-day default - far longer than this conversation's age -
            // so only the release pass should ever touch it.
            // Kept far out of the way so this test proves only the widget path.
            DefaultChannelInactivityWindow = TimeSpan.FromDays(365),
            // `25-118`: see RunOnceAsync_TheWindowDiffersByChannelKind_OnlyTheConversationPastItsOwnWindowCloses's
            // own remarks - a large batch so this test's own candidate cannot be crowded out by
            // leftover rows other tests in this shared fixture left behind.
            BatchSize = 100_000,
        }).RunOnceAsync(CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var conversation = await verify.Conversations.SingleAsync(c => c.Id == seeded.ConversationId);
        Assert.Equal(ConversationState.Waiting, conversation.State);
        Assert.Null(conversation.OperatorId);

        // `26-138`: the release pass stamps the marker (last_sequence 1 - the visitor's single "hi",
        // SeedAssignedConversationAsync adds no operator reply) so the automatic assignment engine will
        // not re-claim this quiet conversation until a newer visitor message arrives.
        Assert.Equal(1, conversation.ReleasedWaitingAtSequence);

        // The outbox row is ConversationReleasedToQueueMapper's own ConversationReleasedToQueue - the
        // release pass's own contract, distinct from the close pass's ConversationEnded.
        var outboxRow = await verify.Set<OutboxMessage>().SingleAsync(o => o.PartitionKey == seeded.ConversationId.Value.ToString());
        Assert.Equal(nameof(ConversationReleasedToQueue), outboxRow.Type);
        Assert.Null(outboxRow.PublishedAt);

        Assert.Equal(0, await ReadActiveChatsAsync(seeded.OperatorId));
    }

    /// <summary>`25-118`'s own fails-before table, row 2: before this item,
    /// `AutoCloseInactiveConversationsQuery` structurally could never select a `Waiting` row at all
    /// (`state = 'Assigned'` only) - reverting `FindStaleWidgetBatchIncludingWaitingAsync` back to that
    /// shape, or skipping the job's new close pass, reproduces that and this test fails because nothing
    /// ever touches the seeded conversation. Proves: a widget conversation already sitting in `Waiting`
    /// (via `25-118`'s own release pass, or `4-04`'s pre-existing disconnect release - this test seeds
    /// it directly, exactly as `RunOnceAsync_LeavesAWaitingConversationAlone_RegardlessOfAge` above used
    /// to for the old, now-superseded "never touched" claim) is actually closed once genuinely past
    /// `WidgetCloseWindow`.</summary>
    [Fact]
    public async Task RunOnceAsync_ClosesAWaitingWidgetConversationPastWidgetCloseWindow()
    {
        var widgetCloseWindow = TimeSpan.FromHours(1);
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var createdAt = Now - widgetCloseWindow - TimeSpan.FromMinutes(1);

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(visitorId, siteId, createdAt));
            // `25-221`: a brand-new conversation starts Pending, not Waiting - graduate it with the
            // visitor's own real first message before persisting it, so it is genuinely Waiting for
            // this test's own close pass (which now also reaches Waiting rows) to find.
            var seeded = Conversation.Start(conversationId, siteId, visitorId, createdAt);
            seeded.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), createdAt);
            db.Conversations.Add(seeded);
            await db.SaveChangesAsync();
        }

        await CreateJob(new AutoCloseInactiveConversationsJobOptions
        {
            // Kept far out of the way - this conversation was never Assigned, so the release pass
            // (which only ever scans Assigned rows) has nothing to do with this test either way.
            WidgetInactivityWindow = TimeSpan.FromDays(365),
            WidgetCloseWindow = widgetCloseWindow,
            // `25-118`: see RunOnceAsync_TheWindowDiffersByChannelKind_OnlyTheConversationPastItsOwnWindowCloses's
            // own remarks - a large batch so this test's own candidate cannot be crowded out by
            // leftover rows other tests in this shared fixture left behind.
            BatchSize = 100_000,
        }).RunOnceAsync(CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var conversation = await verify.Conversations.SingleAsync(c => c.Id == conversationId);
        Assert.Equal(ConversationState.Closed, conversation.State);

        var outboxRow = await verify.Set<OutboxMessage>().SingleAsync(o => o.Id == conversationId.Value);
        Assert.Equal(nameof(ConversationEnded), outboxRow.Type);
    }

    /// <summary>The negative pairing for the fails-before test right above: a widget conversation
    /// sitting in `Waiting`, but not yet past `WidgetCloseWindow`, is left alone - the whole point of
    /// having two windows rather than closing every `Waiting` widget conversation this job ever
    /// sees.</summary>
    [Fact]
    public async Task RunOnceAsync_LeavesAWaitingWidgetConversationAlone_WhenNotYetPastWidgetCloseWindow()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var createdAt = Now - TimeSpan.FromMinutes(1);

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(visitorId, siteId, createdAt));
            // `25-221`: a brand-new conversation starts Pending, not Waiting - graduate it with the
            // visitor's own real first message before persisting it, so this test proves a genuinely
            // Waiting conversation is left alone, not merely that an invisible Pending one is.
            var seeded = Conversation.Start(conversationId, siteId, visitorId, createdAt);
            seeded.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), createdAt);
            db.Conversations.Add(seeded);
            await db.SaveChangesAsync();
        }

        await CreateJob(new AutoCloseInactiveConversationsJobOptions
        {
            WidgetInactivityWindow = TimeSpan.FromDays(365),
            WidgetCloseWindow = TimeSpan.FromDays(7),
            // `25-118`: see RunOnceAsync_TheWindowDiffersByChannelKind_OnlyTheConversationPastItsOwnWindowCloses's
            // own remarks - a large batch so this test's own candidate cannot be crowded out by
            // leftover rows other tests in this shared fixture left behind. (This particular test only
            // asserts the negative - the conversation stays untouched - so a starved batch would not
            // actually make it fail; set anyway, for the same defensive reason and to keep the four
            // sibling tests in this file consistent.)
            BatchSize = 100_000,
        }).RunOnceAsync(CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var conversation = await verify.Conversations.SingleAsync(c => c.Id == conversationId);
        Assert.Equal(ConversationState.Waiting, conversation.State);
    }

    [Fact]
    public async Task RunOnceAsync_LeavesAnAssignedConversationAlone_WhenAMessageArrivedInsideTheWindow()
    {
        var widgetWindow = TimeSpan.FromHours(1);
        // The conversation itself is old; only the message is recent - proves the job looks at last
        // message activity, not conversation age.
        var seeded = await SeedAssignedConversationAsync(
            createdAt: Now - widgetWindow - TimeSpan.FromHours(2), holdsCapacityClaim: true);

        await using (var db = fixture.CreateDbContext())
        {
            var conversation = await db.Conversations.Include("_messages")
                .SingleAsync(c => c.Id == seeded.ConversationId);
            conversation.AddVisitorMessage(
                seeded.VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("still here"),
                Now - TimeSpan.FromMinutes(1));
            await db.SaveChangesAsync();
        }

        await CreateJob(new AutoCloseInactiveConversationsJobOptions { WidgetInactivityWindow = widgetWindow })
            .RunOnceAsync(CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var conversation2 = await verify.Conversations.SingleAsync(c => c.Id == seeded.ConversationId);
        Assert.Equal(ConversationState.Assigned, conversation2.State);
        Assert.Equal(1, await ReadActiveChatsAsync(seeded.OperatorId));
    }

    /// <summary>`18-06`'s own Done-when: a short widget window and a longer channel window, both
    /// conversations equally stale by age, only the one past its <em>own</em> threshold closes.
    /// `25-118`: <c>WidgetCloseWindow</c> is set equal to <c>WidgetInactivityWindow</c> here,
    /// deliberately - this test's own point is channel-kind-vs-widget window independence, which the
    /// release/close split (covered by its own dedicated tests above) would otherwise obscure if the
    /// widget conversation only ever reached `Waiting` and never actually closed.
    ///
    /// <para><b>`25-118`: found genuinely failing, not flaky, once enough of this file's own other
    /// tests ran first in the same suite.</b> `PostgresFixture`'s own remarks: one container per
    /// collection, "every test isolates itself with fresh ids instead" of truncating between tests -
    /// so every widget-shaped `Waiting`/`Assigned` row any earlier test in this whole assembly ever
    /// left behind (this file's own negative-case tests leave one behind on purpose, forever, as their
    /// entire point) is still sitting in `conversations` when this test's own job runs, and
    /// `AutoCloseInactiveConversationsQuery`'s scan is deliberately cross-tenant/unscoped (its own
    /// remarks) - it has no way to tell "this test's own row" apart from any other. With the default
    /// `BatchSize` (100) and `ORDER BY c.created_at ASC`, enough older leftover rows crowd this test's
    /// own (comparatively young, `staleFor`-old) widget conversation out of the batch entirely, and it
    /// is left `Waiting` instead of reaching `Closed` - reproduced by running this whole assembly's
    /// full suite, not by running this one test alone. A very large explicit `BatchSize` below is the
    /// fix: production's default of 100 makes sense against a real, ever-draining backlog (a closed
    /// conversation stops matching the very next tick), but a shared test fixture that only ever grows
    /// needs a batch wide enough to never be starved by that growth.</para>
    /// </summary>
    [Fact]
    public async Task RunOnceAsync_TheWindowDiffersByChannelKind_OnlyTheConversationPastItsOwnWindowCloses()
    {
        var staleFor = TimeSpan.FromMinutes(30);
        var createdAt = Now - staleFor;

        var widget = await SeedAssignedConversationAsync(createdAt, holdsCapacityClaim: false);
        var channel = await SeedAssignedConversationAsync(
            createdAt, holdsCapacityClaim: false, channelKind: ChannelKind.Max, channelAddress: "max-user-1");

        await CreateJob(new AutoCloseInactiveConversationsJobOptions
        {
            WidgetInactivityWindow = TimeSpan.FromMinutes(10),       // shorter than staleFor: past due
            WidgetCloseWindow = TimeSpan.FromMinutes(10),            // same - so the widget conversation fully closes, not just releases
            DefaultChannelInactivityWindow = TimeSpan.FromHours(24), // far longer than staleFor: not due
            // See this test's own remarks: large enough that this test's own candidate can never be
            // crowded out of the batch by leftover rows earlier tests in this shared fixture left behind.
            BatchSize = 100_000,
        }).RunOnceAsync(CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var widgetConversation = await verify.Conversations.SingleAsync(c => c.Id == widget.ConversationId);
        var channelConversation = await verify.Conversations.SingleAsync(c => c.Id == channel.ConversationId);

        Assert.Equal(ConversationState.Closed, widgetConversation.State);
        Assert.Equal(ConversationState.Assigned, channelConversation.State);
    }

    /// <summary>`18-06`'s hardest Done-when: auto-close a channel-linked conversation, then feed the
    /// same channel identity a new inbound message through the real end-to-end path
    /// (<c>ReceiveChannelMessageHandler</c> -> <c>StartConversationHandler</c> ->
    /// <c>ConversationRepository.GetActiveForVisitorAsync</c>'s own <c>State != Closed</c> filter) and
    /// show a <em>new</em> conversation opens, still linked to the <em>same</em> <c>VisitorId</c> - the
    /// closed conversation is never touched again, not resurrected, not merged into.</summary>
    [Fact]
    public async Task RunOnceAsync_ThenANewInboundMessageFromTheSameChannelIdentity_OpensANewConversation_LinkedToTheSameVisitor()
    {
        var channelWindow = TimeSpan.FromHours(24);
        const string address = "sms-user-1";
        var seeded = await SeedAssignedConversationAsync(
            createdAt: Now - channelWindow - TimeSpan.FromHours(1), holdsCapacityClaim: true,
            channelKind: ChannelKind.Sms, channelAddress: address);

        await CreateJob(new AutoCloseInactiveConversationsJobOptions
        {
            WidgetInactivityWindow = TimeSpan.FromDays(365),
            DefaultChannelInactivityWindow = channelWindow,
        }).RunOnceAsync(CancellationToken.None);

        await using (var verify = fixture.CreateDbContext())
        {
            var closed = await verify.Conversations.SingleAsync(c => c.Id == seeded.ConversationId);
            Assert.Equal(ConversationState.Closed, closed.State);
        }

        Assert.Equal(0, await ReadActiveChatsAsync(seeded.OperatorId));

        await using var db = fixture.CreateDbContext();
        var receiveChannelMessage = new ReceiveChannelMessageHandler(
            new ChannelIdentityRepository(db),
            new VisitorRepository(db),
            new PendingChannelLinkRequestRepository(db),
            // `23-78`: real SiteRepository/no-op cache - never exercised for what this test actually
            // checks (StartConversationHandler's own tenant-default read just answers "not found" for
            // every site this fixture never seeds one for).
            new StartConversationHandler(
                new VisitorRepository(db), new ConversationRepository(db), new VisitorRestrictionRepository(fixture.DataSource),
                new GetSiteConfigByIdHandler(new SiteRepository(db), new NoOpCache()),
                new FakeRateLimiter(), new ConversationCreateRateLimitOptions(), new SystemClock(),
                new UuidV7Generator(), new VisitorEmojiPairGenerator(), new EfOutboxWriter<AgoChatDbContext>(db),
                new ChannelIdentityRepository(db)),
            new SendVisitorMessageHandler(
                new ConversationRepository(db), new FakeRateLimiter(), new MessageSendRateLimitOptions(),
                new SynchronousMessagePipeline(fixture.DataSource)),
            new AlwaysEntitledBillingOptionEntitlementProvider(),
            new AlwaysEntitledModuleQuantityGrantStore(),
            new SystemClock(),
            new UuidV7Generator(),
            new VisitorEmojiPairGenerator());

        var result = await receiveChannelMessage.HandleAsync(
            new ReceiveChannelMessage(
                seeded.SiteId, ChannelKind.Sms, new ExternalChannelAddress(address),
                new ExternalMessageId("mid-after-auto-close"), "still there?"),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : "");
        Assert.Equal(seeded.VisitorId, result.Value.VisitorId);
        Assert.NotEqual(seeded.ConversationId.Value, result.Value.ConversationId.Value);

        await using var final = fixture.CreateDbContext();
        var oldConversation = await final.Conversations.SingleAsync(c => c.Id == seeded.ConversationId);
        var newConversation = await final.Conversations.SingleAsync(c => c.Id == result.Value.ConversationId);

        // The old row is untouched by the new message - still Closed, still the same visitor - and
        // the new one is a genuinely separate conversation for that same visitor, not a reopen.
        Assert.Equal(ConversationState.Closed, oldConversation.State);
        Assert.Equal(seeded.VisitorId, oldConversation.VisitorId);
        Assert.Equal(seeded.VisitorId, newConversation.VisitorId);
        Assert.NotEqual(ConversationState.Closed, newConversation.State);
    }

    /// <summary>`26-232`'s own fails-before table, row 1 - and the direct reversal of what this test
    /// used to prove (see git history: before this item, a `Waiting` channel-kind conversation was
    /// asserted to stay `Waiting` "regardless of age", because
    /// `AutoCloseInactiveConversationsQuery`'s channel scan structurally selected `state = 'Assigned'`
    /// only). That was the actual bug this item exists to fix: an operator-disconnect release (`4-04`)
    /// or any other path back to `Waiting` left a channel conversation stuck there forever, piling up in
    /// "Ожидают" with no way out. Reverting <see cref="AutoCloseInactiveConversationsQuery.FindStaleChannelBatchIncludingWaitingAsync"/>
    /// back to Assigned-only (or skipping it) reproduces that and this test fails. Proves: a channel-kind
    /// conversation sitting in `Waiting`, past its own per-`ChannelKind` window
    /// (<see cref="AutoCloseInactiveConversationsJobOptions.WindowFor"/> - <see cref="AutoCloseInactiveConversationsJobOptions.DefaultChannelInactivityWindow"/>
    /// here, since no per-kind override is configured), is actually closed - the same single window the
    /// channel-kind bucket has always used, now also reachable from `Waiting`, not just `Assigned`.
    /// </summary>
    [Fact]
    public async Task RunOnceAsync_ClosesAWaitingChannelKindConversationPastItsWindow()
    {
        var channelWindow = TimeSpan.FromHours(1);
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var createdAt = Now - channelWindow - TimeSpan.FromMinutes(1);

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(visitorId, siteId, createdAt));
            db.ChannelIdentities.Add(ChannelIdentity.Link(
                new ChannelIdentityId(Guid.NewGuid()), siteId, ChannelKind.Max,
                new ExternalChannelAddress("max-user-waiting-past-window"), visitorId, createdAt));
            // `25-221`: a brand-new conversation starts Pending, not Waiting - graduate it with the
            // visitor's own real first message before persisting it, so it is genuinely Waiting for
            // this test's own close pass (which now also reaches Waiting rows for channel kinds) to find.
            var seeded = Conversation.Start(conversationId, siteId, visitorId, createdAt);
            seeded.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), createdAt);
            db.Conversations.Add(seeded);
            await db.SaveChangesAsync();
        }

        await CreateJob(new AutoCloseInactiveConversationsJobOptions
        {
            // Kept far out of the way - this conversation is a channel conversation, never widget, so
            // neither widget window has anything to do with it either way.
            WidgetInactivityWindow = TimeSpan.FromDays(365),
            WidgetCloseWindow = TimeSpan.FromDays(365),
            DefaultChannelInactivityWindow = channelWindow,
            BatchSize = 100_000,
        }).RunOnceAsync(CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var conversation = await verify.Conversations.SingleAsync(c => c.Id == conversationId);
        Assert.Equal(ConversationState.Closed, conversation.State);

        var outboxRow = await verify.Set<OutboxMessage>().SingleAsync(o => o.Id == conversationId.Value);
        Assert.Equal(nameof(ConversationEnded), outboxRow.Type);
    }

    /// <summary>The negative pairing for the fails-before test right above, mirroring
    /// <see cref="RunOnceAsync_LeavesAWaitingWidgetConversationAlone_WhenNotYetPastWidgetCloseWindow"/>
    /// for the channel-kind bucket: a channel-kind conversation sitting in `Waiting`, but not yet past
    /// its own window, is left alone - `26-232` gives channel-kind conversations a real close window,
    /// not an immediate close the instant they are found in `Waiting`.</summary>
    [Fact]
    public async Task RunOnceAsync_LeavesAWaitingChannelKindConversationAlone_WhenNotYetPastItsWindow()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var createdAt = Now - TimeSpan.FromMinutes(1);

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(visitorId, siteId, createdAt));
            db.ChannelIdentities.Add(ChannelIdentity.Link(
                new ChannelIdentityId(Guid.NewGuid()), siteId, ChannelKind.Max,
                new ExternalChannelAddress("max-user-waiting-within-window"), visitorId, createdAt));
            // `25-221`: graduate Pending -> Waiting with a real first message, same as this file's other
            // directly-seeded-Waiting tests, so this proves a genuinely Waiting conversation is left
            // alone, not merely that an invisible Pending one is.
            var seeded = Conversation.Start(conversationId, siteId, visitorId, createdAt);
            seeded.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), createdAt);
            db.Conversations.Add(seeded);
            await db.SaveChangesAsync();
        }

        await CreateJob(new AutoCloseInactiveConversationsJobOptions
        {
            WidgetInactivityWindow = TimeSpan.FromDays(365),
            WidgetCloseWindow = TimeSpan.FromDays(365),
            DefaultChannelInactivityWindow = TimeSpan.FromHours(24),
            BatchSize = 100_000,
        }).RunOnceAsync(CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var conversation = await verify.Conversations.SingleAsync(c => c.Id == conversationId);
        Assert.Equal(ConversationState.Waiting, conversation.State);
    }

    private AutoCloseInactiveConversationsJob CreateJob(AutoCloseInactiveConversationsJobOptions options) => new(
        fixture.DataSource,
        new DirectScopeFactory(fixture, new FixedClock(Now)),
        new FixedClock(Now),
        Options.Create(options),
        NullLogger<AutoCloseInactiveConversationsJob>.Instance);

    private async Task<int> ReadActiveChatsAsync(OperatorId operatorId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT active_chats FROM operators WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", operatorId.Value);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private sealed record Seeded(SiteId SiteId, VisitorId VisitorId, OperatorId OperatorId, ConversationId ConversationId);

    private async Task<Seeded> SeedAssignedConversationAsync(
        DateTimeOffset createdAt, bool holdsCapacityClaim, ChannelKind? channelKind = null, string? channelAddress = null)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(visitorId, siteId, createdAt));
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));

            if (channelKind is { } kind)
            {
                db.ChannelIdentities.Add(ChannelIdentity.Link(
                    new ChannelIdentityId(Guid.NewGuid()), siteId, kind,
                    new ExternalChannelAddress(channelAddress ?? $"addr-{Guid.NewGuid():N}"), visitorId, createdAt));
            }

            var conversation = Conversation.Start(conversationId, siteId, visitorId, createdAt);
            // `25-221`: a brand-new conversation starts Pending, not Waiting - graduate it with the
            // visitor's own real first message before AssignTo, which still only accepts Waiting.
            conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), createdAt);
            conversation.AssignTo(operatorId, createdAt, holdsCapacityClaim);
            conversation.ClearDomainEvents();
            db.Conversations.Add(conversation);

            await db.SaveChangesAsync();
        }

        if (holdsCapacityClaim)
        {
            // active_chats is a shadow property (4-01) - seed it directly to match the AssignTo call
            // above, since EF never writes it - the same setup OperatorConversationReleaserTests uses.
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("UPDATE operators SET active_chats = 1 WHERE id = @id", connection);
            command.Parameters.AddWithValue("id", operatorId.Value);
            await command.ExecuteNonQueryAsync();
        }

        return new Seeded(siteId, visitorId, operatorId, conversationId);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    /// <summary>
    /// The job's own production shape resolves `AutoCloseConversationHandler` from a fresh
    /// `IServiceScopeFactory` scope per candidate (the job's own remarks on why: a captured scoped
    /// handler in a singleton hosted service would share one `DbContext`/change-tracker across every
    /// close for the life of the process). This test double reproduces exactly that - one fresh
    /// `AgoChatDbContext` per scope, wired to real `ConversationRepository`/`OperatorCapacityStore`/
    /// `EfOutboxWriter` against `fixture`'s real Postgres container - without pulling in a full ASP.NET
    /// Core DI container for a two-line test fake: a real `IServiceProvider` implementation would add
    /// ceremony this scope's one known consumer (`AutoCloseConversationHandler`) does not need.
    /// </summary>
    /// <summary>`25-118`: now resolves two handlers, not one - <see cref="AutoCloseConversationHandler"/>
    /// for both close passes (widget and channel-kind), and <see cref="ReleaseInactiveConversationHandler"/>
    /// for the new widget release pass. Both share the identical "one fresh <c>AgoChatDbContext</c> per
    /// scope, real repositories against <c>fixture</c>'s Postgres" reasoning this class's own remarks
    /// already give.</summary>
    private sealed class DirectScopeFactory(PostgresFixture fixture, IClock clock) : IServiceScopeFactory
    {
        public IServiceScope CreateScope()
        {
            var db = fixture.CreateDbContext();
            var autoClose = new AutoCloseConversationHandler(
                new ConversationRepository(db),
                new OperatorCapacityStore(db),
                new EfOutboxWriter<AgoChatDbContext>(db),
                new UuidV7Generator(),
                clock,
                new GetSiteConfigByIdHandler(new SiteRepository(db), new NoOpCache()),
                NullLogger<AutoCloseConversationHandler>.Instance);
            var release = new ReleaseInactiveConversationHandler(
                new ConversationRepository(db),
                new ConversationAssignmentLog(db),
                new OperatorCapacityStore(db),
                new EfOutboxWriter<AgoChatDbContext>(db),
                new UuidV7Generator(),
                clock,
                NullLogger<ReleaseInactiveConversationHandler>.Instance);
            return new DirectScope(db, autoClose, release);
        }

        private sealed class DirectScope(
            AgoChatDbContext db, AutoCloseConversationHandler autoClose, ReleaseInactiveConversationHandler release) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new TwoServiceProvider(autoClose, release);

            public void Dispose() => db.Dispose();
        }

        private sealed class TwoServiceProvider(AutoCloseConversationHandler autoClose, ReleaseInactiveConversationHandler release)
            : IServiceProvider
        {
            public object? GetService(Type serviceType) =>
                serviceType.IsInstanceOfType(autoClose) ? autoClose :
                serviceType.IsInstanceOfType(release) ? release :
                null;
        }
    }
}
