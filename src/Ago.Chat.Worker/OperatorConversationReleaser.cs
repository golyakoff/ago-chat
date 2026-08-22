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
            conversation.ReleaseToQueue(now);

            var domainEvent = conversation.DomainEvents.OfType<ConversationReleased>().Last();
            outbox.Enqueue(ConversationReleasedToQueueMapper.ToEnvelope(domainEvent, siteId, visitorId, idGenerator));
            conversation.ClearDomainEvents();

            await conversations.SaveAsync(conversation, cancellationToken);
            await capacity.ReleaseAsync(operatorId, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return assigned.Count;
    }
}
