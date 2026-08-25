using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `4-04`: releases every conversation currently `Assigned` to one operator back to `Waiting`, and
/// releases their capacity - one Postgres transaction covering all of it, so a failure partway
/// through leaks nothing (matching `SkipLockedAssignmentClaimer`/`RedisLockAssignmentClaimer`'s own
/// reasoning: `IOperatorCapacity.ReleaseAsync` and each `Conversation.SaveAsync` must commit
/// together, or a crash between them either leaks a phantom-occupied slot forever or frees capacity
/// with no record of which conversation it belonged to).
/// </summary>
public sealed class OperatorConversationReleaser(NpgsqlDataSource dataSource, IClock clock, IIdGenerator idGenerator)
{
    public async Task<int> ReleaseAllAsync(OperatorId operatorId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var dbOptions = new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(connection).Options;
        await using var db = new AgoChatDbContext(dbOptions);
        await db.Database.UseTransactionAsync(transaction, cancellationToken);

        var conversations = new ConversationRepository(db);
        var capacity = new OperatorCapacityStore(db);
        var outbox = new EfOutboxWriter<AgoChatDbContext>(db);
        var now = clock.UtcNow;

        var assigned = await conversations.GetAssignedToOperatorAsync(operatorId, cancellationToken);
        foreach (var conversation in assigned)
        {
            var siteId = conversation.SiteId;
            var visitorId = conversation.VisitorId;
            var consumedCapacityClaim = conversation.ReleaseToQueue(now);

            var domainEvent = conversation.DomainEvents.OfType<ConversationReleased>().Last();
            outbox.Enqueue(ConversationReleasedToQueueMapper.ToEnvelope(domainEvent, siteId, visitorId, idGenerator));
            conversation.ClearDomainEvents();

            await conversations.SaveAsync(conversation, cancellationToken);

            // `6-09`: conditional, where this used to decrement once per assigned conversation
            // unconditionally - which asks for more decrements than there were claims whenever the
            // operator picked a conversation up by hand (AssignConversationHandler takes no capacity
            // claim at all). This sweep happened to survive that because it releases *every* one of
            // the operator's assignments and ReleaseAsync floors at zero, so the extra decrements had
            // nothing left to eat. Changed anyway, and deliberately: "one release per claim" is now a
            // rule CloseConversationHandler depends on, and a second path quietly obeying a different
            // rule that only agrees by accident is how the two drift apart later. The receipt is the
            // rule; the floor is a backstop, not the mechanism.
            //
            // Not replaced by a flat `SET active_chats = 0` for this operator, which looks like the
            // stronger repair and is actually unsafe: the assigned-conversation list above was read
            // before this transaction touched the operators row, so a claim another Worker replica
            // committed in between belongs to a conversation this sweep will not release, and zeroing
            // the counter would strand it.
            if (consumedCapacityClaim)
            {
                await capacity.ReleaseAsync(operatorId, cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return assigned.Count;
    }
}
