using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `23-03`'s <see cref="IConversationAssignmentLog"/> - EF change-tracking, not raw SQL, and
/// deliberately so: see the port's own remarks on why neither method here calls
/// <c>SaveChangesAsync</c>. <see cref="Open"/> adds a tracked entity; <see cref="CloseOpenAsync"/>
/// loads one and mutates it. Both ride whatever <c>SaveChangesAsync</c> (or explicit transaction) the
/// caller's own <see cref="AgoChatDbContext"/> commits next - the same instance
/// <c>ConversationRepository</c>/<c>OperatorCapacityStore</c> were constructed with for the same
/// request or batch, exactly like every other adapter in this project that needs to land inside a
/// caller-owned unit of work rather than open its own.
/// </summary>
public sealed class ConversationAssignmentLog(AgoChatDbContext db) : IConversationAssignmentLog
{
    public void Open(ConversationAssignmentInterval interval) =>
        db.Set<ConversationAssignmentInterval>().Add(interval);

    public async Task CloseOpenAsync(ConversationId conversationId, DateTimeOffset endedAt, CancellationToken cancellationToken)
    {
        // At most one row can match: a conversation has at most one operator at a time, so it has at
        // most one open interval. No match at all is the honest, expected state for a conversation
        // assigned before this item shipped - see the port's own remarks on why that is a no-op, not
        // an error.
        var open = await db.Set<ConversationAssignmentInterval>()
            .FirstOrDefaultAsync(i => i.ConversationId == conversationId && i.EndedAt == null, cancellationToken);
        open?.Close(endedAt);
    }
}
