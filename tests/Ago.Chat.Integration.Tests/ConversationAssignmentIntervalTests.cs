using Ago.Chat.Application.UseCases.AssignConversation;
using Ago.Chat.Application.UseCases.CloseConversation;
using Ago.Chat.Application.UseCases.TransferConversation;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-03`'s own Done-when, proven against a real Postgres rather than the in-memory fakes
/// <c>Ago.Chat.Application.Tests</c> uses: every real writer of `conversation_assignments` opens or
/// closes exactly the interval it should, and the interval commits in the identical transaction as the
/// conversation's own state change - the claim <see cref="IConversationAssignmentLog"/>'s own remarks
/// make and that only a real database can actually check.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ConversationAssignmentIntervalTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private async Task<(SiteId SiteId, OperatorId OperatorId, ConversationId ConversationId)> SeedWaitingConversationAsync(
        Permission permission, int capacity = 5)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var roleId = Guid.NewGuid();

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity));
        db.Roles.Add(new RoleRecord { Id = roleId, SiteId = siteId, Name = "Operator", Permissions = [permission.Value] });
        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
        db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
        await db.SaveChangesAsync();

        return (siteId, operatorId, conversationId);
    }

    [Fact]
    public async Task AssignConversationHandler_WhenClaimingAWaitingConversation_OpensAnInterval()
    {
        var (siteId, operatorId, conversationId) = await SeedWaitingConversationAsync(Permission.ConversationAssign);

        await using (var db = fixture.CreateDbContext())
        {
            var handler = new AssignConversationHandler(
                new ConversationRepository(db), new ConversationAssignmentLog(db), new PermissionChecker(db),
                new UuidV7Generator(), new SystemClock());

            var result = await handler.HandleAsync(
                new AssignConversation(conversationId, operatorId, siteId), CancellationToken.None);
            Assert.True(result.IsSuccess);
        }

        await using var verify = fixture.CreateDbContext();
        var interval = await verify.ConversationAssignments.AsNoTracking()
            .SingleAsync(i => i.ConversationId == conversationId);
        Assert.Equal(operatorId, interval.OperatorId);
        Assert.Equal(siteId, interval.SiteId);
        Assert.Equal(ConversationAssignmentSource.Assigned, interval.Source);
        Assert.Null(interval.EndedAt);
    }

    /// <summary>`23-03`'s own Done-when: "A hub reconnect by the same operator adds no row" - proven
    /// here against real Postgres by calling the handler twice, the same shape
    /// <c>OperatorHub.JoinConversationAsync</c> produces on every reconnect.</summary>
    [Fact]
    public async Task AssignConversationHandler_WhenTheSameOperatorReconnects_OpensNoSecondInterval()
    {
        var (siteId, operatorId, conversationId) = await SeedWaitingConversationAsync(Permission.ConversationAssign);
        var command = new AssignConversation(conversationId, operatorId, siteId);

        await using (var db = fixture.CreateDbContext())
        {
            var handler = new AssignConversationHandler(
                new ConversationRepository(db), new ConversationAssignmentLog(db), new PermissionChecker(db),
                new UuidV7Generator(), new SystemClock());
            Assert.True((await handler.HandleAsync(command, CancellationToken.None)).IsSuccess);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var handler = new AssignConversationHandler(
                new ConversationRepository(db), new ConversationAssignmentLog(db), new PermissionChecker(db),
                new UuidV7Generator(), new SystemClock());
            Assert.True((await handler.HandleAsync(command, CancellationToken.None)).IsSuccess);
        }

        await using var verify = fixture.CreateDbContext();
        var count = await verify.ConversationAssignments.CountAsync(i => i.ConversationId == conversationId);
        Assert.Equal(1, count);
    }

    /// <summary>`23-03`'s own Done-when: "A conversation closed while held leaves no open interval" -
    /// and the interval close lands in the same transaction as the conversation's own `Closed` state
    /// (both flushed by the identical `SaveChangesAsync` call inside `CloseConversationHandler`).</summary>
    [Fact]
    public async Task CloseConversationHandler_WhenClosingAnAssignedConversation_ClosesTheOpenInterval()
    {
        var (siteId, operatorId, conversationId) = await SeedWaitingConversationAsync(Permission.ConversationClose);
        var intervalId = new ConversationAssignmentId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            var conversation = await db.Conversations.SingleAsync(c => c.Id == conversationId);
            conversation.AssignTo(operatorId, Now);
            conversation.ClearDomainEvents();
            db.ConversationAssignments.Add(ConversationAssignmentInterval.Open(
                intervalId, siteId, conversationId, operatorId, ConversationAssignmentSource.Assigned, Now));
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            var handler = new CloseConversationHandler(
                new ConversationRepository(db), new ConversationAssignmentLog(db), new PermissionChecker(db),
                new OperatorCapacityStore(db), new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator(),
                new SystemClock(), NullLogger<CloseConversationHandler>.Instance);

            var result = await handler.HandleAsync(
                new CloseConversation(conversationId, operatorId, siteId), CancellationToken.None);
            Assert.True(result.IsSuccess);
        }

        await using var verify = fixture.CreateDbContext();
        var interval = await verify.ConversationAssignments.AsNoTracking().SingleAsync(i => i.Id == intervalId);
        var conversationRow = await verify.Conversations.AsNoTracking().SingleAsync(c => c.Id == conversationId);
        Assert.NotNull(interval.EndedAt);
        Assert.Equal(conversationRow.ClosedAt, interval.EndedAt);
        Assert.Equal(0, await verify.ConversationAssignments.CountAsync(i => i.ConversationId == conversationId && i.EndedAt == null));
    }

    /// <summary>`23-03`'s own Done-when: "A transfer leaves two rows: the first with an `ended_at`, the
    /// second open, and they do not overlap beyond the transaction's own instant" - both stamped with
    /// the identical `IClock` reading (`TransferConversationHandler`'s own remarks), so the departing
    /// operator's `EndedAt` and the receiving operator's `StartedAt` are exactly equal, never merely
    /// close.</summary>
    [Fact]
    public async Task TransferConversationHandler_LeavesTwoNonOverlappingRows()
    {
        var (siteId, fromOperatorId, conversationId) = await SeedWaitingConversationAsync(Permission.ConversationAssign);
        var toOperatorId = new OperatorId(Guid.NewGuid());
        var fromIntervalId = new ConversationAssignmentId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            db.Operators.Add(new Operator(toOperatorId, siteId, OperatorStatus.Online, capacity: 5));
            var conversation = await db.Conversations.SingleAsync(c => c.Id == conversationId);
            conversation.AssignTo(fromOperatorId, Now);
            conversation.ClearDomainEvents();
            db.ConversationAssignments.Add(ConversationAssignmentInterval.Open(
                fromIntervalId, siteId, conversationId, fromOperatorId, ConversationAssignmentSource.Assigned, Now));
            await db.SaveChangesAsync();
        }

        var transferredAt = Now.AddMinutes(5);
        await using (var db = fixture.CreateDbContext())
        {
            var handler = new TransferConversationHandler(
                new ConversationRepository(db), new OperatorRepository(db), new ConversationAssignmentLog(db),
                new PermissionChecker(db), new OperatorCapacityStore(db), new EfUnitOfWork(db),
                new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator(), new FixedClock(transferredAt));

            var result = await handler.HandleAsync(
                new TransferConversation(conversationId, fromOperatorId, toOperatorId, siteId), CancellationToken.None);
            Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : string.Empty);
        }

        await using var verify = fixture.CreateDbContext();
        var intervals = await verify.ConversationAssignments.AsNoTracking()
            .Where(i => i.ConversationId == conversationId)
            .ToListAsync();
        Assert.Equal(2, intervals.Count);

        var fromInterval = Assert.Single(intervals, i => i.OperatorId == fromOperatorId);
        var toInterval = Assert.Single(intervals, i => i.OperatorId == toOperatorId);
        Assert.Equal(transferredAt, fromInterval.EndedAt);
        Assert.Null(toInterval.EndedAt);
        Assert.Equal(transferredAt, toInterval.StartedAt);
        Assert.Equal(ConversationAssignmentSource.Transferred, toInterval.Source);
        // Zero overlap: the departing operator's interval ends at the exact instant the receiving
        // operator's begins, never a moment later.
        Assert.Equal(fromInterval.EndedAt, toInterval.StartedAt);
    }

    /// <summary>A fixed-instant <see cref="IClock"/>, since <see cref="TransferConversationHandler"/>
    /// reads <c>clock.UtcNow</c> once and reuses it for both the state change and the interval
    /// close/open - a real <see cref="SystemClock"/> would make the "exactly equal, not merely close"
    /// assertion above flaky by a few ticks.</summary>
    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
