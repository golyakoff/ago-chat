using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Worker;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `4-01`'s direct proof that <see cref="WaitingConversationClaimQuery"/> actually behaves like a
/// `SKIP LOCKED` claim, not just that its SQL parses - two real, concurrently open transactions
/// against the same site's waiting rows, not one transaction called twice sequentially.
/// </summary>
[Collection(PostgresCollection.Name)]
public class WaitingConversationClaimQueryTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ClaimBatchAsync_ReturnsWaitingConversationsForTheSite_OldestFirst_UpToBatchSize()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var expectedOrder = await SeedWaitingConversationsAsync(siteId, count: 3);

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var claimed = await WaitingConversationClaimQuery.ClaimBatchAsync(
            connection, transaction, siteId, batchSize: 2, CancellationToken.None);

        Assert.Equal(expectedOrder.Take(2), claimed);
        await transaction.CommitAsync();
    }

    [Fact]
    public async Task ClaimBatchAsync_IgnoresAssignedAndClosedConversations_AndOtherSites()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var otherSiteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var waitingId = (await SeedWaitingConversationsAsync(siteId, count: 1)).Single();

        await using (var db = fixture.CreateDbContext())
        {
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
            var visitorId = new VisitorId(Guid.NewGuid());
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            // `25-221`: a brand-new conversation starts Pending, not Waiting - graduate it with the
            // visitor's own real first message before AssignTo, which still only accepts Waiting.
            var assigned = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, Now);
            assigned.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
            assigned.AssignTo(operatorId, Now);
            db.Conversations.Add(assigned);

            var closedVisitorId = new VisitorId(Guid.NewGuid());
            db.Visitors.Add(new Visitor(closedVisitorId, siteId, Now));
            var closed = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, closedVisitorId, Now);
            closed.Close(Now);
            db.Conversations.Add(closed);

            db.Sites.Add(new Site(otherSiteId, $"site_{otherSiteId.Value:N}", []));
            var otherVisitorId = new VisitorId(Guid.NewGuid());
            db.Visitors.Add(new Visitor(otherVisitorId, otherSiteId, Now));
            // Genuinely Waiting too - this row must be excluded for being the wrong site, not merely
            // for being Pending.
            var otherSiteConversation = Conversation.Start(new ConversationId(Guid.NewGuid()), otherSiteId, otherVisitorId, Now);
            otherSiteConversation.AddVisitorMessage(otherVisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
            db.Conversations.Add(otherSiteConversation);

            await db.SaveChangesAsync();
        }

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var claimed = await WaitingConversationClaimQuery.ClaimBatchAsync(
            connection, transaction, siteId, batchSize: 10, CancellationToken.None);

        Assert.Equal([waitingId], claimed);
        await transaction.CommitAsync();
    }

    [Fact]
    public async Task ClaimBatchAsync_SkipsRowsAlreadyLockedByAnotherOpenTransaction()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var ids = await SeedWaitingConversationsAsync(siteId, count: 3);

        await using var connectionA = await fixture.DataSource.OpenConnectionAsync();
        await using var transactionA = await connectionA.BeginTransactionAsync();
        var claimedByA = await WaitingConversationClaimQuery.ClaimBatchAsync(
            connectionA, transactionA, siteId, batchSize: 2, CancellationToken.None);
        Assert.Equal(2, claimedByA.Count);

        // transactionA is still open (not committed) - its claim's row locks are still held, so a
        // second, concurrently open transaction must skip them rather than blocking or double-claiming.
        await using var connectionB = await fixture.DataSource.OpenConnectionAsync();
        await using var transactionB = await connectionB.BeginTransactionAsync();
        var claimedByB = await WaitingConversationClaimQuery.ClaimBatchAsync(
            connectionB, transactionB, siteId, batchSize: 10, CancellationToken.None);

        Assert.Equal([ids[2]], claimedByB);
        Assert.Empty(claimedByA.Intersect(claimedByB));

        await transactionB.CommitAsync();
        await transactionA.CommitAsync();
    }

    /// <summary>`24-10`'s own decided reading of its open question: "an inbound message from a blocked
    /// visitor... is not routed" - a blocked conversation must never be claimed by the automatic
    /// assignment engine, even though blocking never touches its own `state` column (a blocked
    /// conversation can perfectly well still be `Waiting`).</summary>
    [Fact]
    public async Task ClaimBatchAsync_IgnoresABlockedWaitingConversation()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var ids = await SeedWaitingConversationsAsync(siteId, count: 2);
        var blockedId = ids[0];
        var stillClaimableId = ids[1];

        var blocks = new ConversationBlockRepository(fixture.DataSource);
        var outcome = await blocks.BlockAsync(
            blockedId, siteId, new OperatorId(Guid.NewGuid()), Guid.NewGuid(), Now, CancellationToken.None);
        Assert.Equal(ConversationBlockOutcome.Applied, outcome);

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var claimed = await WaitingConversationClaimQuery.ClaimBatchAsync(
            connection, transaction, siteId, batchSize: 10, CancellationToken.None);

        Assert.Equal([stillClaimableId], claimed);
        await transaction.CommitAsync();
    }

    /// <summary>`23-69`/`23-77`'s own direct proof of the fails-before both backlog items name: a
    /// conversation a restricted visitor's <c>StartConversationHandler</c> call silently created
    /// (<c>Conversation.RoutingSuppressedAt</c> stamped at construction, the identical "must never be
    /// claimed" guarantee <see cref="ClaimBatchAsync_IgnoresABlockedWaitingConversation"/> right above
    /// already proves for `24-10`'s own, separate flag) is never picked up by the automatic assignment
    /// engine either - proven against the real `routing_suppressed_at IS NULL` predicate, not just that
    /// the flag exists on the aggregate.</summary>
    [Fact]
    public async Task ClaimBatchAsync_IgnoresARoutingSuppressedWaitingConversation()
    {
        var siteId = new SiteId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));

            // `25-221`: a brand-new conversation starts Pending, not Waiting - graduate each with the
            // visitor's own real first message before persisting it, so both are genuinely Waiting and
            // this test proves routing suppression itself, not merely Pending's own invisibility.
            var suppressedVisitorId = new VisitorId(Guid.NewGuid());
            db.Visitors.Add(new Visitor(suppressedVisitorId, siteId, Now));
            var suppressed = Conversation.Start(
                new ConversationId(Guid.NewGuid()), siteId, suppressedVisitorId, Now, suppressRouting: true);
            suppressed.AddVisitorMessage(suppressedVisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
            db.Conversations.Add(suppressed);

            var ordinaryVisitorId = new VisitorId(Guid.NewGuid());
            db.Visitors.Add(new Visitor(ordinaryVisitorId, siteId, Now));
            var ordinary = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, ordinaryVisitorId, Now);
            ordinary.AddVisitorMessage(ordinaryVisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
            db.Conversations.Add(ordinary);

            await db.SaveChangesAsync();

            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var claimed = await WaitingConversationClaimQuery.ClaimBatchAsync(
                connection, transaction, siteId, batchSize: 10, CancellationToken.None);

            Assert.Equal([ordinary.Id], claimed);
            await transaction.CommitAsync();
        }
    }

    /// <summary>`25-221`'s own fails-before/passes-after proof, at the level nearest the reported bug:
    /// opening a conversation alone (<see cref="Conversation.Start"/>, unconditionally
    /// <see cref="ConversationState.Pending"/> since this item - exactly what a widget mount alone
    /// produces via <c>VisitorHub.JoinAsync</c>) must never be picked up by a real assignment cycle -
    /// <see cref="ConversationAssignmentJob.RunOnceAsync"/>, through the real
    /// <see cref="SkipLockedAssignmentClaimer"/> production actually runs, not the raw query the other
    /// tests in this file exercise directly. Only once the visitor's own first real message exists does
    /// the identical cycle claim it - proven by running the same job twice, before and after that
    /// message, against one real Postgres row.</summary>
    [Fact]
    public async Task ConversationAssignmentJob_NeverAssignsAConversationWithNoRealMessage_ButAssignsItOnceOneArrives()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var roleId = Guid.NewGuid();

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
            // `25-170`: a real Operator-role seat - the claimer requires one regardless of status, so
            // there is a genuine, eligible candidate for the job to (wrongly, before this item) assign.
            db.Roles.Add(new RoleRecord { Id = roleId, SiteId = siteId, Name = "Operator", Permissions = [] });
            db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
            // The literal reported bug: a brand-new conversation with no message at all.
            db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
            await db.SaveChangesAsync();
        }

        var job = new ConversationAssignmentJob(
            fixture.DataSource,
            new SkipLockedAssignmentClaimer(fixture.DataSource, new SystemClock(), new UuidV7Generator()),
            Options.Create(new ConversationAssignmentJobOptions()), NullLogger<ConversationAssignmentJob>.Instance);

        // Fails-before: a real assignment cycle runs, an eligible operator is online and seated, and
        // still nothing is claimed - the conversation is Pending, not Waiting, structurally invisible
        // to WaitingConversationClaimQuery's own literal `state = 'Waiting'` filter.
        await job.RunOnceAsync(CancellationToken.None);

        await using (var afterFirstCycle = fixture.CreateDbContext())
        {
            var stillPending = await afterFirstCycle.Conversations.AsNoTracking()
                .SingleAsync(c => c.Id == conversationId);
            Assert.Equal(ConversationState.Pending, stillPending.State);
            Assert.Null(stillPending.OperatorId);
        }

        // The visitor writes - the one and only thing that graduates Pending -> Waiting
        // (Conversation.AddVisitorMessage's own remarks).
        await using (var messageDb = fixture.CreateDbContext())
        {
            var repository = new ConversationRepository(messageDb);
            var conversation = await repository.GetByIdAsync(conversationId, CancellationToken.None);
            conversation!.AddVisitorMessage(
                visitorId, new MessageId(Guid.NewGuid()), new MessageBody("is anyone there?"), Now.AddSeconds(1));
            await repository.SaveAsync(conversation, CancellationToken.None);
        }

        // Passes-after: the identical cycle, against the identical row, now claims it.
        await job.RunOnceAsync(CancellationToken.None);

        await using var afterSecondCycle = fixture.CreateDbContext();
        var nowAssigned = await afterSecondCycle.Conversations.AsNoTracking()
            .SingleAsync(c => c.Id == conversationId);
        Assert.Equal(ConversationState.Assigned, nowAssigned.State);
        Assert.Equal(operatorId, nowAssigned.OperatorId);
    }

    /// <summary>`26-119`'s own direct proof, at the raw-query level: two `Waiting` conversations that
    /// both reached `Waiting` through `AutoCloseInactiveConversationsJob.ReleaseStaleAssignedWidgetBatchAsync`'s
    /// release path (an `Assigned` conversation's own <see cref="Conversation.ReleaseToQueue"/>, not
    /// the fresh-queue-entry path <see cref="SeedWaitingConversationsAsync"/> uses) - one where the
    /// operator answered and the visitor simply went quiet (idle-released, nothing pending), one where
    /// the visitor wrote again after being released and nobody has answered yet (genuinely waiting).
    /// Only the second is claimable - the first must never re-enter the churn `26-83` measured.</summary>
    [Fact]
    public async Task ClaimBatchAsync_IgnoresAnIdleReleasedConversation_ButStillClaimsOneWithAPendingVisitorMessage()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        ConversationId idleReleasedId;
        ConversationId pendingInboundId;

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));

            // Idle-released: visitor wrote, the operator answered, the visitor went quiet, and
            // AutoCloseInactiveConversationsJob's own release pass moved this back to Waiting purely
            // for inactivity - the operator's own reply is still the latest message. Must stay
            // unclaimed.
            var idleVisitorId = new VisitorId(Guid.NewGuid());
            db.Visitors.Add(new Visitor(idleVisitorId, siteId, Now));
            idleReleasedId = new ConversationId(Guid.NewGuid());
            var idleReleased = Conversation.Start(idleReleasedId, siteId, idleVisitorId, Now);
            idleReleased.AddVisitorMessage(idleVisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
            idleReleased.AssignTo(operatorId, Now);
            idleReleased.AddOperatorMessage(operatorId, new MessageId(Guid.NewGuid()), new MessageBody("how can I help?"), Now.AddSeconds(1));
            idleReleased.ReleaseToQueue(Now.AddMinutes(10));
            db.Conversations.Add(idleReleased);

            // Genuinely waiting: same release history, but the visitor wrote again afterward and
            // nobody has answered that new message yet. Must still be claimable.
            var pendingVisitorId = new VisitorId(Guid.NewGuid());
            db.Visitors.Add(new Visitor(pendingVisitorId, siteId, Now));
            pendingInboundId = new ConversationId(Guid.NewGuid());
            var pendingInbound = Conversation.Start(pendingInboundId, siteId, pendingVisitorId, Now);
            pendingInbound.AddVisitorMessage(pendingVisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
            pendingInbound.AssignTo(operatorId, Now);
            pendingInbound.AddOperatorMessage(operatorId, new MessageId(Guid.NewGuid()), new MessageBody("how can I help?"), Now.AddSeconds(1));
            pendingInbound.ReleaseToQueue(Now.AddMinutes(10));
            pendingInbound.AddVisitorMessage(pendingVisitorId, new MessageId(Guid.NewGuid()), new MessageBody("still there?"), Now.AddMinutes(11));
            db.Conversations.Add(pendingInbound);

            await db.SaveChangesAsync();
        }

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var claimed = await WaitingConversationClaimQuery.ClaimBatchAsync(
            connection, transaction, siteId, batchSize: 10, CancellationToken.None);

        Assert.Equal([pendingInboundId], claimed);
        await transaction.CommitAsync();
    }

    /// <summary>`26-119`'s own Done-when, proven end to end through the real
    /// <see cref="ConversationAssignmentJob"/>/<see cref="SkipLockedAssignmentClaimer"/> pair rather than
    /// the raw query alone - the same "nearest the reported bug" level
    /// <see cref="ConversationAssignmentJob_NeverAssignsAConversationWithNoRealMessage_ButAssignsItOnceOneArrives"/>
    /// already uses for `25-221`'s own regression. First tick: an idle-released conversation (operator
    /// already answered, visitor gone quiet) must not be re-claimed - it stays `Waiting`, unassigned,
    /// exactly like `26-83` diagnosed it should have all along. Second tick, after the visitor writes
    /// again: the identical conversation, now genuinely owed a reply, is assigned exactly as before.
    /// </summary>
    [Fact]
    public async Task ConversationAssignmentJob_NeverReassignsAnIdleReleasedConversation_ButAssignsItOnceANewVisitorMessageArrives()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var roleId = Guid.NewGuid();

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
            // `25-170`: a real Operator-role seat - the claimer requires one regardless of status.
            db.Roles.Add(new RoleRecord { Id = roleId, SiteId = siteId, Name = "Operator", Permissions = [] });
            db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });

            // Assigned, answered, then released for inactivity - AutoCloseInactiveConversationsJob's
            // own release pass, modelled directly through the same Conversation.ReleaseToQueue call it
            // uses (via ReleaseInactiveConversationHandler), not the job itself: this test's own
            // concern is the assignment side of the churn, not the release side, which
            // AutoCloseInactiveConversationsJobTests already covers.
            var conversation = Conversation.Start(conversationId, siteId, visitorId, Now);
            conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
            conversation.AssignTo(operatorId, Now);
            conversation.AddOperatorMessage(operatorId, new MessageId(Guid.NewGuid()), new MessageBody("how can I help?"), Now.AddSeconds(1));
            conversation.ReleaseToQueue(Now.AddMinutes(10));
            db.Conversations.Add(conversation);

            await db.SaveChangesAsync();
        }

        var job = new ConversationAssignmentJob(
            fixture.DataSource,
            new SkipLockedAssignmentClaimer(fixture.DataSource, new SystemClock(), new UuidV7Generator()),
            Options.Create(new ConversationAssignmentJobOptions()), NullLogger<ConversationAssignmentJob>.Instance);

        // Fails-before this item: the release above put the conversation back in Waiting, an eligible
        // operator is online and seated with room, and a real assignment cycle still must not touch it -
        // nothing is actually pending.
        await job.RunOnceAsync(CancellationToken.None);

        await using (var afterFirstCycle = fixture.CreateDbContext())
        {
            var stillWaiting = await afterFirstCycle.Conversations.AsNoTracking()
                .SingleAsync(c => c.Id == conversationId);
            Assert.Equal(ConversationState.Waiting, stillWaiting.State);
            Assert.Null(stillWaiting.OperatorId);
        }

        // The visitor writes again - the new inbound is the signal this item's own backlog text names
        // as what should make it claimable again.
        await using (var messageDb = fixture.CreateDbContext())
        {
            var repository = new ConversationRepository(messageDb);
            var conversation = await repository.GetByIdAsync(conversationId, CancellationToken.None);
            conversation!.AddVisitorMessage(
                visitorId, new MessageId(Guid.NewGuid()), new MessageBody("still there?"), Now.AddMinutes(11));
            await repository.SaveAsync(conversation, CancellationToken.None);
        }

        // Passes-after: the identical cycle, against the identical row, now claims it.
        await job.RunOnceAsync(CancellationToken.None);

        await using var afterSecondCycle = fixture.CreateDbContext();
        var nowAssigned = await afterSecondCycle.Conversations.AsNoTracking()
            .SingleAsync(c => c.Id == conversationId);
        Assert.Equal(ConversationState.Assigned, nowAssigned.State);
        Assert.Equal(operatorId, nowAssigned.OperatorId);
    }

    private async Task<List<ConversationId>> SeedWaitingConversationsAsync(SiteId siteId, int count)
    {
        var ids = new List<ConversationId>();
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));

        for (var i = 0; i < count; i++)
        {
            var visitorId = new VisitorId(Guid.NewGuid());
            var conversationId = new ConversationId(Guid.NewGuid());
            db.Visitors.Add(new Visitor(visitorId, siteId, Now.AddSeconds(i)));
            // `25-221`: a brand-new conversation starts Pending, not Waiting - graduate it with the
            // visitor's own real first message before persisting it, so it is genuinely Waiting (this
            // helper's own name) for WaitingConversationClaimQuery to find.
            var conversation = Conversation.Start(conversationId, siteId, visitorId, Now.AddSeconds(i));
            conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now.AddSeconds(i));
            db.Conversations.Add(conversation);
            ids.Add(conversationId);
        }

        await db.SaveChangesAsync();
        return ids;
    }
}
