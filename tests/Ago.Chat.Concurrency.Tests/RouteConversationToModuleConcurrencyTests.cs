using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.RouteConversationToModule;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>
/// `25-34`: the real reproduction of the live production bug - a picked worker in the calendar booking
/// flow producing "a person will take over from here" instead of the time-picker step, traced to an
/// unhandled `DbUpdateConcurrencyException` on the `Conversation` row's own `xmin` inside
/// `RouteConversationToModuleHandler.AddSystemMessageAndSaveAsync`. Two scenarios, both against a real
/// Postgres row through the real handler (never `EfInboxChecker` in isolation, and never faked
/// concurrency) - the backlog item's own explicit requirement, after two earlier attempts against
/// `EfInboxChecker` alone failed to reproduce anything at all.
/// </summary>
[Collection(ConcurrencyCollection.Name)]
public sealed class RouteConversationToModuleConcurrencyTests(ConcurrencyTestFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);
    private static readonly ModuleKey Calendar = new("calendar");
    private static readonly Uri EntryPoint = new("https://calendar.example.com");
    private static readonly ModuleCredential Credential = new("a-shared-secret-of-sixteen-plus-chars");

    private static readonly ModuleStep ReplyStep = new(
        new MessageContentKind(PrimitiveKinds.ChoiceList),
        new MessagePayload("""{"prompt":"Which time slot?"}"""),
        [new MessageAction("10:00", "t1"), new MessageAction("11:00", "t2")]);

    /// <summary>
    /// The item's own named scenario, literally: two genuinely concurrent deliveries of the *identical*
    /// trigger message (a redelivery racing the original, `adr/0017`'s at-least-once), each in its own
    /// `RouteConversationToModuleHandler` instance with its own `AgoChatDbContext`, both loading and
    /// continuing the same active module task.
    ///
    /// <para><b>Honestly: this one does not fail before the fix, in this harness.</b> Run six times
    /// against the pre-fix handler it passed six times - `EfInboxChecker`'s own dedup-row unique
    /// constraint happened to be the statement that lost first, every time, in this in-process
    /// `Task.Run`-behind-a-gate race, which the pre-fix code already handled correctly (that catch is
    /// exactly what this item's own root-cause writeup credits with "already handles correctly, and has
    /// tests for"). This matches the item's own recorded experience: two earlier attempts to force the
    /// live `DbUpdateConcurrencyException` via `Task.WhenAll` on `EfInboxChecker` alone, before this fix
    /// existed, did not reproduce it either - the live incident needed real production concurrency this
    /// harness cannot force on demand. What this test *does* prove, reliably, both before and after the
    /// fix: the dedup guarantee itself holds under concurrency, not just under the sequential redelivery
    /// <c>RouteConversationToModuleHandlerTests.HandleAsync_ARedeliveredTrigger_ProducesNoSecondEffect</c>
    /// (Application-level, fakes) already covers - see <see cref="TwoDifferentTriggersForTheSameActiveTask_RaceOnTheConversationRow_BothSucceedViaTheRetry"/>
    /// below for the test that *does* reliably fail before this fix and pass after it.</para>
    /// </summary>
    [Fact]
    public async Task TwoConcurrentDeliveriesOfTheIdenticalTrigger_ProduceExactlyOneMessage_WithNoUnhandledException()
    {
        var seed = await SeedActiveTaskConversationAsync();
        var enabledModule = new EnabledModuleSummary(Calendar, ["/booking"], EntryPoint, Credential, GrantedByOwner: false, ExpiresAt: null);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var d1 = Task.Run(async () =>
        {
            await gate.Task;
            return await RouteAsync(seed.SiteId, seed.ConversationId, enabledModule, seed.TriggerMessageId, seed.TriggerSequence);
        });
        var d2 = Task.Run(async () =>
        {
            await gate.Task;
            return await RouteAsync(seed.SiteId, seed.ConversationId, enabledModule, seed.TriggerMessageId, seed.TriggerSequence);
        });

        gate.SetResult();
        var results = await Task.WhenAll(d1, d2);

        Assert.All(results, r => Assert.True(r.IsSuccess, r.IsFailure ? $"{r.Error!.Value.Code}: {r.Error!.Value.Message}" : "success"));
        Assert.Contains(results, r => r.Value == RouteConversationToModuleOutcome.StepAdvanced);
        Assert.Contains(results, r => r.Value == RouteConversationToModuleOutcome.AlreadyProcessed);

        await using var verify = fixture.CreateDbContext();
        var conversationRow = await verify.Conversations
            .Include("_messages")
            .AsNoTracking()
            .SingleAsync(c => c.Id == seed.ConversationId);
        Assert.Single(conversationRow.Messages, m => m.AuthorKind == MessageAuthorKind.System);
    }

    /// <summary>
    /// The general case `RouteConversationToModuleHandler.AddSystemMessageAndSaveAsync`'s own retry
    /// exists for once the dedup row above has already excluded "this is the same trigger racing
    /// itself": two genuinely *different* trigger messages (two rapid visitor replies to the same
    /// active task - a double-tap, or two channels racing) that both mutate the same `Conversation` row
    /// concurrently. Their dedup keys differ, so both pass the check above and both reach the real
    /// `xmin` race - this is what actually exercises the reload-and-retry path
    /// `CloseConversationHandler` established and this item's fix reuses, rather than the dedup gate
    /// above resolving it first.
    ///
    /// <para><b>This is the one that reliably fails before the fix, five runs out of five.</b> Against
    /// the pre-fix handler this consistently loses one message outright - not a thrown exception, a
    /// silent drop: the loser's own `SaveChangesAsync` (staged through `EfInboxChecker`, unrelated
    /// entities and all) hits `messages`' own `(conversation_id, sequence, site_id)` unique index before
    /// its `Conversation` row's own `xmin` check ever runs (the identical "an Added entity's INSERT
    /// executes before a Modified entity's UPDATE within one `SaveChangesAsync`"
    /// <c>ConversationRepository.SaveAsync</c>'s own `23-04` comment already documents for the
    /// assignment-interval case) - and `EfInboxChecker`'s own unique-violation catch, scoped to *its own*
    /// row's dedup semantics, does not distinguish that from a genuine duplicate delivery, so it reports
    /// `AlreadyProcessed` for a message that was never actually persisted. Exactly the failure mode the
    /// backlog item's own "what was tried and discarded" section warns a swallow-in-`EfInboxChecker` fix
    /// would produce - found here as an *existing* property of the pre-fix code under this specific
    /// race, not introduced by this item. `25-34`'s fix closes it two ways at once: routing the
    /// conversation's own save through <c>IConversationRepository.SaveAsync</c> takes it off
    /// `EfInboxChecker`'s shared `SaveChangesAsync` entirely, and the message-sequence catch this item
    /// added to <c>ConversationRepository.SaveAsync</c> (found live, by this exact test, mid-implementation)
    /// means even a same-batch collision on that index now translates to a retryable
    /// <c>ConversationConcurrencyConflictException</c> rather than an untranslated `23505`.</para>
    /// </summary>
    [Fact]
    public async Task TwoDifferentTriggersForTheSameActiveTask_RaceOnTheConversationRow_BothSucceedViaTheRetry()
    {
        var seed = await SeedActiveTaskConversationAsync(secondTrigger: true);
        var enabledModule = new EnabledModuleSummary(Calendar, ["/booking"], EntryPoint, Credential, GrantedByOwner: false, ExpiresAt: null);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var d1 = Task.Run(async () =>
        {
            await gate.Task;
            return await RouteAsync(seed.SiteId, seed.ConversationId, enabledModule, seed.TriggerMessageId, seed.TriggerSequence);
        });
        var d2 = Task.Run(async () =>
        {
            await gate.Task;
            return await RouteAsync(seed.SiteId, seed.ConversationId, enabledModule, seed.SecondTriggerMessageId!.Value, seed.SecondTriggerSequence!.Value);
        });

        gate.SetResult();
        var results = await Task.WhenAll(d1, d2);

        // `assert on that real outcome, don't assume` (this item's own instruction): a genuine two-writer
        // race resolves either as two clean successes (one direct, one via the single retry) or, on the
        // rare occasion a third statement lands inside that already-narrow retry window, a clean
        // Conversation.ConcurrencyConflict result - never a raw, unhandled exception either way.
        Assert.All(results, r => Assert.True(
            r.IsSuccess || r.Error!.Value.Code == "Conversation.ConcurrencyConflict",
            r.IsFailure ? $"{r.Error!.Value.Code}: {r.Error!.Value.Message}" : "success"));

        var successCount = results.Count(r => r.IsSuccess);
        Assert.True(successCount is 1 or 2, $"expected 1 or 2 successes, got {successCount}");

        await using var verify = fixture.CreateDbContext();
        var conversationRow = await verify.Conversations
            .Include("_messages")
            .AsNoTracking()
            .SingleAsync(c => c.Id == seed.ConversationId);
        // Exactly one system message per successful delivery - never fewer (a lost update) and never
        // more (a retry that double-applied its own mutation against stale, pre-conflict state).
        Assert.Equal(successCount, conversationRow.Messages.Count(m => m.AuthorKind == MessageAuthorKind.System));
    }

    /// <summary>
    /// Done-when's own third guarantee, proven against real Postgres rather than only
    /// `RouteConversationToModuleHandlerTests.HandleAsync_ARedeliveredTrigger_ProducesNoSecondEffect`'s
    /// fakes: a genuinely duplicate delivery - not a race, an ordinary redelivery of a trigger the first
    /// call already fully processed, sequenced normally, one call awaited to completion before the
    /// second ever starts - is still recognised and skipped. Unaffected by this item's reordering in
    /// either direction (the dedup row is already recorded by the time the second call even starts), so
    /// this passes both before and after the fix - it guards against a regression in the ordinary case
    /// while the two tests above cover the concurrent one.
    /// </summary>
    [Fact]
    public async Task ASequentialRedeliveryOfAnAlreadyProcessedTrigger_IsSkipped_NoSecondMessage()
    {
        var seed = await SeedActiveTaskConversationAsync();
        var enabledModule = new EnabledModuleSummary(Calendar, ["/booking"], EntryPoint, Credential, GrantedByOwner: false, ExpiresAt: null);

        var first = await RouteAsync(seed.SiteId, seed.ConversationId, enabledModule, seed.TriggerMessageId, seed.TriggerSequence);
        Assert.True(first.IsSuccess, first.IsFailure ? $"{first.Error!.Value.Code}: {first.Error!.Value.Message}" : "success");
        Assert.Equal(RouteConversationToModuleOutcome.StepAdvanced, first.Value);

        var redelivery = await RouteAsync(seed.SiteId, seed.ConversationId, enabledModule, seed.TriggerMessageId, seed.TriggerSequence);
        Assert.True(redelivery.IsSuccess);
        Assert.Equal(RouteConversationToModuleOutcome.AlreadyProcessed, redelivery.Value);

        await using var verify = fixture.CreateDbContext();
        var conversationRow = await verify.Conversations
            .Include("_messages")
            .AsNoTracking()
            .SingleAsync(c => c.Id == seed.ConversationId);
        Assert.Single(conversationRow.Messages, m => m.AuthorKind == MessageAuthorKind.System);
    }

    private sealed record Seed(
        SiteId SiteId, ConversationId ConversationId, Guid TriggerMessageId, int TriggerSequence,
        Guid? SecondTriggerMessageId = null, int? SecondTriggerSequence = null);

    /// <summary>Seeds a Site, Visitor and Conversation with an already-open module task awaiting a
    /// choice reply, plus one (or two, for the retry test) real visitor messages already sequenced as
    /// the trigger(s) - exactly the shape a picked-worker reply arrives in against a live calendar
    /// booking task.</summary>
    private async Task<Seed> SeedActiveTaskConversationAsync(bool secondTrigger = false)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, Now);
        conversation.StartModuleTask(
            new ModuleTaskId(Guid.NewGuid()), Calendar, "external-1", Now,
            new MessageContentKind(PrimitiveKinds.ChoiceList),
            new MessagePayload("""{"prompt":"Which worker?"}"""),
            [new MessageAction("Alex", "worker-1"), new MessageAction("Sam", "worker-2")]);
        var trigger = conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("1"), Now);
        Message? second = null;
        if (secondTrigger)
        {
            second = conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("1"), Now);
        }

        conversation.ClearDomainEvents();

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(CancellationToken.None);

        return new Seed(
            siteId, conversation.Id, trigger.Id.Value, trigger.Sequence,
            second?.Id.Value, second?.Sequence);
    }

    /// <summary>The real handler on its own <c>AgoChatDbContext</c>, exactly as one worker instance
    /// gets it (`RouteConversationToModuleHandlerTests`'s own comment on this same pattern for the
    /// Application-level fakes) - real <see cref="ConversationRepository"/>,
    /// real <see cref="EfOutboxWriter{TDbContext}"/>, real <see cref="EfInboxChecker{TDbContext}"/>
    /// (`Ago.Platform.Persistence.Postgres`), and a deterministic, in-memory
    /// <see cref="IModuleGateway"/>/<see cref="IEnabledModuleReadStore"/> pair - the module gateway call
    /// itself is not what this item is about (`RouteConversationToModuleHandler`'s own type-level
    /// remarks already document that cost separately), so it is faked rather than run against a second
    /// Testcontainer.</summary>
    private async Task<Result<RouteConversationToModuleOutcome>> RouteAsync(
        SiteId siteId, ConversationId conversationId, EnabledModuleSummary enabledModule, Guid triggerMessageId, int triggerSequence)
    {
        await using var db = fixture.CreateDbContext();
        var handler = new RouteConversationToModuleHandler(
            new ConversationRepository(db),
            new FixedEnabledModuleReadStore(siteId, enabledModule),
            new FixedStepModuleGateway(ReplyStep, complete: false),
            new UnreachableChannelIdentityRepository(),
            new EfOutboxWriter<AgoChatDbContext>(db),
            new EfInboxChecker<AgoChatDbContext>(db, new SystemClock()),
            new SystemClock(),
            new UuidV7Generator(),
            new SiteRepository(db),
            new VisitorContactDetailRepository(db));

        return await handler.HandleAsync(
            new RouteConversationToModule(triggerMessageId, siteId, conversationId, MessageAuthorKind.Visitor, triggerSequence),
            CancellationToken.None);
    }

    private sealed class FixedEnabledModuleReadStore(SiteId siteId, EnabledModuleSummary summary) : IEnabledModuleReadStore
    {
        public Task<IReadOnlyList<EnabledModuleSummary>> GetForSiteAsync(SiteId site, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EnabledModuleSummary>>(site == siteId ? [summary] : []);

        public Task<IReadOnlyList<EnabledModuleDetailSummary>> GetAllForSiteAsync(SiteId site, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not needed by ContinueActiveTaskAsync's own path - only the trigger-match path reads this.");
    }

    /// <summary>Always answers with the same fixed step - both racing deliveries are meant to see the
    /// module answer identically, since the point of both tests is the <see cref="Conversation"/> row's
    /// own race, not any difference in what the module said.</summary>
    private sealed class FixedStepModuleGateway(ModuleStep step, bool complete) : IModuleGateway
    {
        public Task<StartModuleTaskResult> StartTaskAsync(EnabledModuleEndpoint module, StartModuleTaskRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Both tests seed an already-active task - TryStartTaskAsync's path is never reached.");

        public Task<SubmitModuleReplyResult> SubmitReplyAsync(EnabledModuleEndpoint module, SubmitModuleReplyRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new SubmitModuleReplyResult(step, complete));
    }

    /// <summary>Never called - the seeded active step is <see cref="PrimitiveKinds.ChoiceList"/>, not
    /// <see cref="PrimitiveKinds.VerifiedPhoneForm"/>, so <c>ContinueActiveTaskAsync</c>'s phone gate
    /// never reads this port.</summary>
    private sealed class UnreachableChannelIdentityRepository : IChannelIdentityRepository
    {
        public Task<ChannelIdentity?> FindAsync(SiteId siteId, ChannelKind kind, ExternalChannelAddress address, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ChannelIdentity?> FindMostRecentForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ChannelIdentity>> ListActiveForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ChannelIdentity?> GetByIdAsync(ChannelIdentityId id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveAsync(ChannelIdentity identity, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
