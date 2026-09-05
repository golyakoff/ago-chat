using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.AssignConversation;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>
/// `23-04`'s own Done-when, at the only level that can prove it: a real Postgres row racing two real
/// take requests, the same shape <see cref="TransferConversationConcurrencyTests"/> already uses for
/// the neighbouring "two writers, one conversation" contention on this same table.
/// <see cref="TwoOperatorsRacingToTakeTheSameWaitingConversation_ExactlyOneWinsCleanly_AndActiveChatsRisesByExactlyOne"/>
/// is the item's own named scenario, verbatim; the class also proves the invariant the previous design
/// explicitly forbade - a take succeeding, and <c>active_chats</c> ending one higher, when the operator
/// is already at or past <c>capacity</c>.
/// </summary>
[Collection(ConcurrencyCollection.Name)]
public sealed class AssignConversationConcurrencyTests(ConcurrencyTestFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    /// <summary>
    /// The item's own second named scenario, taken literally: two different operators both try to take
    /// the *same* `Waiting` conversation at the same instant. Exactly one may win - the conversation has
    /// exactly one <c>OperatorId</c> - and the loser must get <c>Conversation.InvalidState</c> (not a
    /// generic contention code: the two operators claim two different <c>operators</c> rows, so there is
    /// no `40P01` for either of them to lose here, only the conversation row's own `xmin`), and
    /// <c>active_chats</c> across both operators must have risen by exactly one, never two - the loser's
    /// whole transaction, capacity claim included, rolled back with its lost save.
    /// </summary>
    [Fact]
    public async Task TwoOperatorsRacingToTakeTheSameWaitingConversation_ExactlyOneWinsCleanly_AndActiveChatsRisesByExactlyOne()
    {
        const int capacity = 5;
        var seed = await SeedAsync(operatorCount: 2, capacity, conversationCount: 1);
        var operatorA = seed.OperatorIds[0];
        var operatorB = seed.OperatorIds[1];
        var conversationId = (await WaitingIdsAsync(seed)).Single();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var takeA = Task.Run(async () =>
        {
            await gate.Task;
            return await ClaimAsync(seed, conversationId, operatorA);
        });
        var takeB = Task.Run(async () =>
        {
            await gate.Task;
            return await ClaimAsync(seed, conversationId, operatorB);
        });

        gate.SetResult();
        var results = await Task.WhenAll(takeA, takeB);

        Assert.Single(results, r => r.IsSuccess);
        var loser = Assert.Single(results, r => r.IsFailure);
        Assert.Equal("Conversation.InvalidState", loser.Error!.Value.Code);

        await using var verify = fixture.CreateDbContext();
        var conversationRow = await verify.Conversations.AsNoTracking().SingleAsync(c => c.Id == conversationId);
        Assert.Equal(ConversationState.Assigned, conversationRow.State);
        Assert.True(conversationRow.HoldsCapacityClaim);
        Assert.True(conversationRow.OperatorId == operatorA || conversationRow.OperatorId == operatorB);

        var winner = conversationRow.OperatorId!.Value;
        var loserOperator = winner == operatorA ? operatorB : operatorA;
        Assert.Equal(1, await ActiveChatsAsync(winner));
        // The operator the loser named never actually got the conversation, so it must never have been
        // charged for it either - the loser's own transaction rolled back in full, capacity claim
        // included.
        Assert.Equal(0, await ActiveChatsAsync(loserOperator));
    }

    /// <summary>
    /// `23-04`'s own Done-when: "A take when <c>active_chats &gt;= capacity</c> succeeds and
    /// <c>active_chats</c> ends one higher" - the invariant the previous design forbade, proven against
    /// a real row rather than argued from <see cref="IOperatorCapacity.ClaimAsync"/>'s own doc comment.
    /// The operator is seeded already sitting exactly at capacity (a prior engine-made claim), so a
    /// <see cref="IOperatorCapacity.TryClaimAsync"/> here would refuse - this is deliberately not that
    /// call.
    /// </summary>
    [Fact]
    public async Task TakingAConversation_WhenTheOperatorIsAlreadyAtCapacity_SucceedsAndActiveChatsEndsOneHigher()
    {
        const int capacity = 2;
        var seed = await SeedAsync(operatorCount: 1, capacity, conversationCount: 1);
        var operatorId = seed.OperatorIds[0];

        await using (var db = fixture.CreateDbContext())
        {
            var store = new OperatorCapacityStore(db);
            await using var tx = await db.Database.BeginTransactionAsync();
            Assert.True(await store.TryClaimAsync(operatorId, CancellationToken.None));
            Assert.True(await store.TryClaimAsync(operatorId, CancellationToken.None));
            await tx.CommitAsync();
        }

        Assert.Equal(capacity, await ActiveChatsAsync(operatorId));
        // A real compare-and-set claim genuinely refuses here - the fixture proves the operator is
        // actually at capacity, not merely seeded to look like it.
        await using (var db = fixture.CreateDbContext())
        {
            Assert.False(await new OperatorCapacityStore(db).TryClaimAsync(operatorId, CancellationToken.None));
        }

        var conversationId = (await WaitingIdsAsync(seed)).Single();
        var result = await ClaimAsync(seed, conversationId, operatorId);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : string.Empty);
        Assert.Equal(capacity + 1, await ActiveChatsAsync(operatorId));

        await using var verify = fixture.CreateDbContext();
        var conversationRow = await verify.Conversations.AsNoTracking().SingleAsync(c => c.Id == conversationId);
        Assert.Equal(ConversationState.Assigned, conversationRow.State);
        Assert.Equal(operatorId, conversationRow.OperatorId);
        Assert.True(conversationRow.HoldsCapacityClaim);

        var interval = await verify.ConversationAssignments.AsNoTracking().SingleAsync(i => i.ConversationId == conversationId);
        Assert.Equal(ConversationAssignmentSource.Taken, interval.Source);
    }

    private sealed record Seed(SiteId SiteId, IReadOnlyList<OperatorId> OperatorIds, VisitorId VisitorId);

    private async Task<Seed> SeedAsync(int operatorCount, int capacity, int conversationCount)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorIds = Enumerable.Range(0, operatorCount).Select(_ => new OperatorId(Guid.NewGuid())).ToList();
        var roleId = Guid.NewGuid();

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        db.Roles.Add(new RoleRecord
        {
            Id = roleId,
            SiteId = siteId,
            Name = "Operator",
            Permissions = [Permission.ConversationAssign.Value],
        });

        foreach (var operatorId in operatorIds)
        {
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity));
            db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
        }

        for (var i = 0; i < conversationCount; i++)
        {
            db.Conversations.Add(Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, Now));
        }

        await db.SaveChangesAsync(CancellationToken.None);
        return new Seed(siteId, operatorIds, visitorId);
    }

    /// <summary>The real handler on its own <c>AgoChatDbContext</c>, exactly as one Api request gets it
    /// - including its own real <see cref="EfUnitOfWork"/>, not a fake, the same reasoning
    /// <see cref="TransferConversationConcurrencyTests"/>'s own identical helper gives.</summary>
    private async Task<Result> ClaimAsync(Seed seed, ConversationId conversationId, OperatorId operatorId)
    {
        await using var db = fixture.CreateDbContext();
        var handler = new AssignConversationHandler(
            new ConversationRepository(db), new ConversationAssignmentLog(db), new PermissionChecker(db),
            new OperatorCapacityStore(db), new EfUnitOfWork(db), new UuidV7Generator(), new SystemClock());

        return await handler.HandleAsync(
            new AssignConversation(conversationId, operatorId, seed.SiteId), CancellationToken.None);
    }

    private async Task<List<ConversationId>> WaitingIdsAsync(Seed seed)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Conversations.AsNoTracking()
            .Where(c => c.SiteId == seed.SiteId && c.State == ConversationState.Waiting)
            .OrderBy(c => c.Id)
            .Select(c => c.Id)
            .ToListAsync();
    }

    private async Task<int> ActiveChatsAsync(OperatorId operatorId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Operators.AsNoTracking()
            .Where(o => o.Id == operatorId)
            .Select(o => EF.Property<int>(o, "active_chats"))
            .SingleAsync();
    }
}
