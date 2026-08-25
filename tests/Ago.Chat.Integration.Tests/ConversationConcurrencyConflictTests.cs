using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.AssignConversation;
using Ago.Chat.Application.UseCases.CloseConversation;
using Ago.Chat.Contracts;
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
/// `6-08`: the race `6-06`'s load-proof run actually hit - a message send commits and bumps a
/// conversation row's `xmin` between a close/assign handler's own read and its save. Reproduced here
/// against a real Postgres container by injecting a genuine second write (through a second
/// <see cref="AgoChatDbContext"/>, its own transaction, committed before the handler's save runs) at
/// the exact point the handler is inside its own <c>SaveAsync</c> call - not by throwing a fake
/// exception, and not by hoping two threads interleave a particular way. <see cref="RacingConversationRepository"/>
/// is the seam: it delegates every read to the real <see cref="ConversationRepository"/> untouched, and
/// on <c>SaveAsync</c> performs the concurrent write first, so the handler's own in-memory copy is
/// provably stale by the time its save reaches Postgres.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ConversationConcurrencyConflictTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    [Fact]
    public async Task CloseConversation_RacedOnceByAConcurrentMessageSend_RetriesAndSucceeds()
    {
        var (siteId, visitorId, operatorId, conversationId) = await SeedAssignedConversationAsync(Permission.ConversationClose);

        await using var db = fixture.CreateDbContext();
        var racingRepository = new RacingConversationRepository(
            new ConversationRepository(db), maxInjections: 1, () => SendConcurrentVisitorMessageAsync(visitorId, conversationId));
        var handler = new CloseConversationHandler(
            racingRepository, new PermissionChecker(db), new OperatorCapacityStore(db),
            new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator(), new SystemClock(),
            NullLogger<CloseConversationHandler>.Instance);

        var result = await handler.HandleAsync(new CloseConversation(conversationId, operatorId, siteId), CancellationToken.None);

        // The clean outcome this item asks for: a single transparent retry against the now-fresh row,
        // not the DbUpdateConcurrencyException that used to reach the caller as a raw 500.
        Assert.True(result.IsSuccess);
        Assert.Equal(2, racingRepository.SaveAttempts);

        await using var verify = fixture.CreateDbContext();
        var conversationRow = await verify.Conversations.Include("_messages").SingleAsync(c => c.Id == conversationId, CancellationToken.None);
        Assert.Equal(ConversationState.Closed, conversationRow.State);
        // The concurrent writer's message is still there - the retry reloaded the real row rather than
        // overwriting it with a stale in-memory copy.
        Assert.Single(conversationRow.Messages);

        // Exactly one ConversationEnded row, not two - the failed first attempt's outbox enqueue never
        // committed (same DbContext, same transaction as its own failed SaveChangesAsync), so only the
        // retry's own enqueue survives.
        var outboxRows = await verify.Set<OutboxMessage>().Where(o => o.Id == conversationId.Value).ToListAsync(CancellationToken.None);
        var outboxRow = Assert.Single(outboxRows);
        Assert.Equal(nameof(ConversationEnded), outboxRow.Type);
    }

    [Fact]
    public async Task CloseConversation_RacedTwiceInARow_ReturnsConcurrencyConflictNotA500()
    {
        var (siteId, visitorId, operatorId, conversationId) = await SeedAssignedConversationAsync(Permission.ConversationClose);

        await using var db = fixture.CreateDbContext();
        // A third writer lands inside the handler's own one-time retry window too - both the original
        // save and the single retry lose the race, so no amount of transparent retrying can succeed
        // honestly. This is the "clean 409, not a 500" half of this item's scope.
        var racingRepository = new RacingConversationRepository(
            new ConversationRepository(db), maxInjections: 2, () => SendConcurrentVisitorMessageAsync(visitorId, conversationId));
        var handler = new CloseConversationHandler(
            racingRepository, new PermissionChecker(db), new OperatorCapacityStore(db),
            new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator(), new SystemClock(),
            NullLogger<CloseConversationHandler>.Instance);

        var result = await handler.HandleAsync(new CloseConversation(conversationId, operatorId, siteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.ConcurrencyConflict", result.Error!.Value.Code);
        Assert.Equal(2, racingRepository.SaveAttempts);

        await using var verify = fixture.CreateDbContext();
        var conversationRow = await verify.Conversations.SingleAsync(c => c.Id == conversationId, CancellationToken.None);
        // Never closed - a definitive failure must not half-apply the state change.
        Assert.Equal(ConversationState.Assigned, conversationRow.State);
        Assert.False(await verify.Set<OutboxMessage>().AnyAsync(o => o.Id == conversationId.Value, CancellationToken.None));
    }

    [Fact]
    public async Task AssignConversation_RacedOnceByAConcurrentMessageSend_RetriesAndSucceeds()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var roleId = Guid.NewGuid();

        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            seed.Visitors.Add(new Visitor(visitorId, siteId, Now));
            seed.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
            seed.Roles.Add(new RoleRecord { Id = roleId, SiteId = siteId, Name = "Operator", Permissions = [Permission.ConversationAssign.Value] });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
            seed.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using var db = fixture.CreateDbContext();
        var racingRepository = new RacingConversationRepository(
            new ConversationRepository(db), maxInjections: 1, () => SendConcurrentVisitorMessageAsync(visitorId, conversationId));
        var handler = new AssignConversationHandler(racingRepository, new PermissionChecker(db), new SystemClock());

        var result = await handler.HandleAsync(new AssignConversation(conversationId, operatorId, siteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, racingRepository.SaveAttempts);

        await using var verify = fixture.CreateDbContext();
        var conversationRow = await verify.Conversations.Include("_messages").SingleAsync(c => c.Id == conversationId, CancellationToken.None);
        Assert.Equal(ConversationState.Assigned, conversationRow.State);
        Assert.Equal(operatorId, conversationRow.OperatorId);
        Assert.Single(conversationRow.Messages);
    }

    private async Task<(SiteId SiteId, VisitorId VisitorId, OperatorId OperatorId, ConversationId ConversationId)> SeedAssignedConversationAsync(Permission permission)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var roleId = Guid.NewGuid();

        await using var seed = fixture.CreateDbContext();
        seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        seed.Visitors.Add(new Visitor(visitorId, siteId, Now));
        seed.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
        seed.Roles.Add(new RoleRecord { Id = roleId, SiteId = siteId, Name = "Operator", Permissions = [permission.Value] });
        seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });

        var conversation = Conversation.Start(conversationId, siteId, visitorId, Now);
        conversation.AssignTo(operatorId, Now);
        conversation.ClearDomainEvents();
        seed.Conversations.Add(conversation);

        await seed.SaveChangesAsync(CancellationToken.None);
        return (siteId, visitorId, operatorId, conversationId);
    }

    /// <summary>The concurrent writer: a fresh <see cref="AgoChatDbContext"/>, its own load, its own
    /// commit - exactly `6-06`'s real root cause (a message send updating the conversation row) rather
    /// than a direct SQL poke that wouldn't exercise the same code path.</summary>
    private async Task SendConcurrentVisitorMessageAsync(VisitorId visitorId, ConversationId conversationId)
    {
        await using var db = fixture.CreateDbContext();
        var repository = new ConversationRepository(db);
        var conversation = await repository.GetByIdAsync(conversationId, CancellationToken.None);
        conversation!.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("racing in"), Now);
        await repository.SaveAsync(conversation, CancellationToken.None);
    }

    /// <summary>Delegates every read to a real <see cref="ConversationRepository"/> untouched; on
    /// <c>SaveAsync</c>, for each of the first <paramref name="maxInjections"/> calls, runs
    /// <paramref name="injectConcurrentWriteAsync"/> to completion (a real commit, on a different
    /// <see cref="AgoChatDbContext"/>) before delegating to the real save - so the handler under test
    /// is provably racing a write that has already landed, not merely might have.</summary>
    private sealed class RacingConversationRepository(
        IConversationRepository inner, int maxInjections, Func<Task> injectConcurrentWriteAsync) : IConversationRepository
    {
        private int _saveAttempts;

        public int SaveAttempts => _saveAttempts;

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
            _saveAttempts++;
            if (_saveAttempts <= maxInjections)
            {
                await injectConcurrentWriteAsync();
            }

            await inner.SaveAsync(conversation, cancellationToken);
        }
    }
}
