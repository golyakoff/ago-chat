using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.MarkConversationRead;
using Ago.Chat.Application.UseCases.RecordUnread;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>
/// `5-15`'s Done-when, at the only level that can prove it: the unread counter's two writers running
/// at the same time against one real Postgres row. <c>RecordUnreadMessageHandler</c> lives in
/// <c>Ago.Chat.Worker</c>, <c>MarkConversationReadHandler</c> in <c>Ago.Chat.Api</c> - two processes,
/// no shared lock, only the row's `xmin`. Reasoning about that is exactly what this item said was not
/// enough.
///
/// <para>Each round here is a real race, not an interleaving simulated by ordering two awaits: both
/// operations open their own DI scope (their own <see cref="AgoChatDbContext"/>, their own
/// transaction - the same shape production gets from a hub request and a consumer message), wait on a
/// shared gate, and are released together onto the thread pool.</para>
///
/// <para><b>What "correct" means</b>, taken verbatim from the item: a message that arrived and was
/// never seen must still be counted. Both tests below assert an exact final number rather than a
/// range, because the design makes them deterministic under <em>every</em> interleaving - that
/// determinism is the actual claim, and a range would hide its failure.</para>
/// </summary>
[Collection(ConcurrencyCollection.Name)]
public sealed class MarkConversationReadConcurrencyTests(ConcurrencyTestFixture fixture, ITestOutputHelper output)
{
    // Real time, not a fixed date: the `messages` table is partitioned by month and only the current
    // month plus the next two have partitions, so a hard-coded 2026-01-01 makes every insert here fail
    // with "no partition of relation messages found for row" (found by writing exactly that first).
    // Truncated to whole seconds so it round-trips through `timestamptz` unchanged.
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    private const int Rounds = 40;

    /// <summary>
    /// The race the item calls out by name: a visitor message landing in the same instant as a
    /// mark-read. The operator marks read up to the newest message they actually have (round
    /// <c>r - 1</c>) while round <c>r</c>'s message is being counted. Load-reset-save loses precisely
    /// here - the retry after an `xmin` conflict would re-apply "set it to zero" on top of the
    /// concurrent increment and the arriving message would vanish from the badge forever.
    /// </summary>
    [Fact]
    public async Task IncrementRacingAMarkRead_StillCountsTheMessageTheOperatorNeverSaw()
    {
        var seed = await SeedAssignedConversationAsync();
        await using var services = fixture.CreateServiceProvider();
        var conflicts = 0;

        for (var round = 1; round <= Rounds; round++)
        {
            var (messageId, sequence) = await AppendVisitorMessageAsync(seed);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var counting = Task.Run(async () =>
            {
                await gate.Task;
                return await RecordUnreadWithBrokerRetryAsync(services, seed, messageId, sequence);
            });
            var reading = Task.Run(async () =>
            {
                await gate.Task;
                // Deliberately `sequence - 1`: the operator has everything up to the message before
                // this round's, and has never seen this round's.
                return await MarkReadAsync(seed, upToSequence: sequence - 1);
            });

            gate.SetResult();
            var attempts = await Task.WhenAll(counting, reading);
            conflicts += attempts.Sum() - 2;
        }

        // Reported, not asserted on: the number varies run to run, and demanding a minimum would make
        // this test flaky on a machine where the two writers happen not to collide. What it does show
        // is whether the race was real on this run rather than merely arranged.
        output.WriteLine(
            $"rounds={Rounds}; increment attempts lost to a real xmin conflict and redelivered={conflicts}");

        var conversation = await LoadAsync(seed.ConversationId);
        // The last mark-read cleared up to Rounds - 1, so exactly one visitor message - the final
        // round's, which the operator never saw - is still outstanding. Not "at least one": every
        // earlier round's message was genuinely read, and an over-count would be just as wrong.
        Assert.Equal(Rounds - 1, conversation.OperatorLastReadSequence);
        Assert.Equal(1, conversation.OperatorUnreadCount);
        Assert.Equal(Rounds, conversation.Messages.Count);
    }

    /// <summary>
    /// The mirror image, and the reason the clear takes a sequence at all: when the operator *does*
    /// have the arriving message on screen, the same race must settle on zero. Whichever of the two
    /// writers commits first, the other re-decides against fresh data - the increment sees a
    /// watermark already past it and skips, or the mark-read subtracts the increment that just
    /// landed.
    /// </summary>
    [Fact]
    public async Task IncrementRacingAMarkReadThatCoversIt_LeavesTheCountAtZero()
    {
        var seed = await SeedAssignedConversationAsync();
        await using var services = fixture.CreateServiceProvider();

        for (var round = 1; round <= Rounds; round++)
        {
            var (messageId, sequence) = await AppendVisitorMessageAsync(seed);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var counting = Task.Run(async () =>
            {
                await gate.Task;
                return await RecordUnreadWithBrokerRetryAsync(services, seed, messageId, sequence);
            });
            var reading = Task.Run(async () =>
            {
                await gate.Task;
                return await MarkReadAsync(seed, upToSequence: sequence);
            });

            gate.SetResult();
            await Task.WhenAll(counting, reading);
        }

        var conversation = await LoadAsync(seed.ConversationId);
        Assert.Equal(Rounds, conversation.OperatorLastReadSequence);
        Assert.Equal(0, conversation.OperatorUnreadCount);
    }

    /// <summary>Many mark-reads at once for the same conversation - what an operator with several
    /// console tabs open produces. The retry-once policy has to absorb them without any of them
    /// failing, and the result has to be the same as if they had run one at a time.</summary>
    [Fact]
    public async Task ConcurrentMarkReadsForTheSameConversation_AllSucceedAndAgreeOnTheResult()
    {
        var seed = await SeedAssignedConversationAsync();
        await using var services = fixture.CreateServiceProvider();
        for (var i = 0; i < 5; i++)
        {
            var (messageId, sequence) = await AppendVisitorMessageAsync(seed);
            await RecordUnreadWithBrokerRetryAsync(services, seed, messageId, sequence);
        }

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readers = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () =>
            {
                await gate.Task;
                return await MarkReadAsync(seed, upToSequence: 5);
            }))
            .ToArray();

        gate.SetResult();
        await Task.WhenAll(readers);

        var conversation = await LoadAsync(seed.ConversationId);
        Assert.Equal(0, conversation.OperatorUnreadCount);
        Assert.Equal(5, conversation.OperatorLastReadSequence);
    }

    /// <summary>
    /// The same race as the first test, but arranged rather than hoped for - the stochastic version
    /// above only actually collided on a handful of its rounds on the machine that wrote it, which is
    /// not a bar to leave a correctness claim resting on. Here a visitor message is appended
    /// <em>and counted</em>, committed on its own connection, at the exact instant the mark-read is
    /// inside <c>SaveAsync</c>: the handler's copy is provably stale, its save provably loses on
    /// `xmin`, and the retry has to re-derive the answer from the row as it now stands.
    ///
    /// <para>This is the test that fails outright for a load-reset-save implementation: reloading and
    /// re-applying "set the count to zero" would report zero for a message that arrived after the
    /// operator's read position and that they have never seen.</para>
    /// </summary>
    [Fact]
    public async Task MarkRead_RacedMidSaveByAMessageTheOperatorHasNotSeen_StillCountsIt()
    {
        var seed = await SeedAssignedConversationAsync();
        await using var services = fixture.CreateServiceProvider();
        for (var i = 0; i < 3; i++)
        {
            var (messageId, sequence) = await AppendVisitorMessageAsync(seed);
            await RecordUnreadWithBrokerRetryAsync(services, seed, messageId, sequence);
        }

        await using var db = fixture.CreateDbContext();
        var racing = new RacingConversationRepository(
            new ConversationRepository(db),
            maxInjections: 1,
            async () =>
            {
                var (messageId, sequence) = await AppendVisitorMessageAsync(seed);
                await RecordUnreadWithBrokerRetryAsync(services, seed, messageId, sequence);
            });
        var handler = new MarkConversationReadHandler(racing, new PermissionChecker(db));

        var result = await handler.HandleAsync(
            new Application.UseCases.MarkConversationRead.MarkConversationRead(
                seed.ConversationId, seed.OperatorId, seed.SiteId, UpToSequence: 3),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        // Two saves: the first lost the race, the second went in against the fresh row.
        Assert.Equal(2, racing.SaveAttempts);
        // The one message that arrived while the operator was marking read - sequence 4, past their
        // read position of 3 - is still counted. This is `5-15`'s "correct" in one number.
        Assert.Equal(1, result.Value.OperatorUnreadCount);
        Assert.Equal(3, result.Value.OperatorLastReadSequence);

        var conversation = await LoadAsync(seed.ConversationId);
        Assert.Equal(1, conversation.OperatorUnreadCount);
        Assert.Equal(3, conversation.OperatorLastReadSequence);
        Assert.Equal(4, conversation.Messages.Count);
    }

    private sealed record Seed(SiteId SiteId, VisitorId VisitorId, OperatorId OperatorId, ConversationId ConversationId);

    /// <summary>`6-08`'s seam, reused verbatim: every read goes to the real repository untouched, and
    /// each of the first <paramref name="maxInjections"/> saves runs a real, fully-committed concurrent
    /// write first.</summary>
    private sealed class RacingConversationRepository(
        IConversationRepository inner, int maxInjections, Func<Task> injectConcurrentWriteAsync) : IConversationRepository
    {
        public int SaveAttempts { get; private set; }

        public Task<Conversation?> GetByIdAsync(ConversationId id, CancellationToken cancellationToken) =>
            inner.GetByIdAsync(id, cancellationToken);

        public Task<Conversation?> GetActiveForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken) =>
            inner.GetActiveForVisitorAsync(visitorId, cancellationToken);

        public Task<IReadOnlyList<Conversation>> GetAssignedToOperatorAsync(OperatorId operatorId, CancellationToken cancellationToken) =>
            inner.GetAssignedToOperatorAsync(operatorId, cancellationToken);

        public Task<IReadOnlyList<Conversation>> GetWaitingForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
            inner.GetWaitingForSiteAsync(siteId, cancellationToken);

        public async Task SaveAsync(Conversation conversation, CancellationToken cancellationToken)
        {
            SaveAttempts++;
            if (SaveAttempts <= maxInjections)
            {
                await injectConcurrentWriteAsync();
            }

            await inner.SaveAsync(conversation, cancellationToken);
        }
    }

    private async Task<Seed> SeedAssignedConversationAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var roleId = Guid.NewGuid();

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
        db.Roles.Add(new RoleRecord
        {
            Id = roleId,
            SiteId = siteId,
            Name = "Operator",
            Permissions = [Permission.ConversationRead.Value],
        });
        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });

        var conversation = Conversation.Start(conversationId, siteId, visitorId, Now);
        conversation.AssignTo(operatorId, Now);
        conversation.ClearDomainEvents();
        db.Conversations.Add(conversation);

        await db.SaveChangesAsync(CancellationToken.None);
        return new Seed(siteId, visitorId, operatorId, conversationId);
    }

    /// <summary>The message itself commits first and on its own, exactly as production does: the row
    /// lands in one transaction (adr/0005) and the `MessageAccepted` that drives the counter is
    /// published, and consumed, afterwards. That gap is what makes the race in these tests real
    /// rather than contrived.</summary>
    private async Task<(Guid MessageId, int Sequence)> AppendVisitorMessageAsync(Seed seed)
    {
        await using var db = fixture.CreateDbContext();
        var repository = new ConversationRepository(db);
        var conversation = await repository.GetByIdAsync(seed.ConversationId, CancellationToken.None);
        var message = conversation!.AddVisitorMessage(
            seed.VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("incoming"), Now);
        conversation.ClearDomainEvents();
        await repository.SaveAsync(conversation, CancellationToken.None);
        return (message.Id.Value, message.Sequence);
    }

    /// <summary>Stands in for the broker: a losing concurrent save propagates out of
    /// <c>RecordUnreadMessageHandler</c> (its own doc comment's stated answer), RabbitMQ redelivers,
    /// and a fresh scope reloads the current row. Retrying here rather than swallowing is what keeps
    /// the increment side honest - a dropped increment would make these tests pass for the wrong
    /// reason. Returns the number of attempts, so the caller can report how much real contention
    /// occurred instead of asserting a race happened without evidence.</summary>
    private static async Task<int> RecordUnreadWithBrokerRetryAsync(
        IServiceProvider services, Seed seed, Guid messageId, int sequence)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var scope = services.CreateAsyncScope();
            try
            {
                var handler = scope.ServiceProvider.GetRequiredService<RecordUnreadMessageHandler>();
                var result = await handler.HandleAsync(
                    new RecordUnreadMessage(messageId, seed.ConversationId, MessageAuthorKind.Visitor, sequence),
                    CancellationToken.None);
                Assert.True(result.IsSuccess);
                return attempt;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 20)
            {
                // Exactly what the broker does on a nack-with-requeue - a new scope, a new read.
            }
        }
    }

    /// <summary>The API side, through the real handler and the real repository - including its own
    /// retry-once, which is why a `Conversation.ConcurrencyConflict` reaching the caller would be a
    /// genuine failure to report rather than something to paper over here.
    ///
    /// Returns a constant `1` purely to match <see cref="RecordUnreadWithBrokerRetryAsync"/>'s shape so
    /// the two can be awaited together as one <c>Task.WhenAll</c>: the handler owns its retry
    /// internally and does not report how many attempts it took, so the contention figure the first
    /// test prints counts increment-side redeliveries only. Understating it is the safe direction -
    /// it is reported, never asserted on.</summary>
    private async Task<int> MarkReadAsync(Seed seed, int upToSequence)
    {
        await using var db = fixture.CreateDbContext();
        var handler = new MarkConversationReadHandler(new ConversationRepository(db), new PermissionChecker(db));
        var result = await handler.HandleAsync(
            new Application.UseCases.MarkConversationRead.MarkConversationRead(
                seed.ConversationId, seed.OperatorId, seed.SiteId, upToSequence),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : string.Empty);
        return 1;
    }

    private async Task<Conversation> LoadAsync(ConversationId conversationId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Conversations.Include("_messages").FirstAsync(c => c.Id == conversationId, CancellationToken.None);
    }
}
