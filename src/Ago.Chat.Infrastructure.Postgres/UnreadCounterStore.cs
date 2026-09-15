using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `25-109`'s <see cref="IUnreadCounterStore"/> - raw SQL through <see cref="AgoChatDbContext"/>'s own
/// connection via <c>ExecuteSqlInterpolatedAsync</c>, the identical mechanism
/// <c>OperatorCapacityStore</c> already uses for the identical reason (that type's own remarks): it
/// participates in <c>Database.CurrentTransaction</c> automatically when the caller has one open
/// (<c>RecordUnreadMessageHandler</c>'s own <see cref="IUnitOfWork"/> transaction), and still commits
/// on its own, standalone, when there is none.
/// </summary>
public sealed class UnreadCounterStore(AgoChatDbContext db) : IUnreadCounterStore
{
    public Task IncrementAsync(
        ConversationId conversationId, MessageAuthorKind authorKind, int sequence, CancellationToken cancellationToken)
    {
        // `5-15`'s own branching, restated as SQL rather than an in-memory mutation - see
        // Conversation.IncrementUnreadCount's own remarks for why the visitor side is unconditional
        // (no watermark to consult) and the operator side is gated on the *current* row, not a value
        // read earlier.
        return authorKind == MessageAuthorKind.Visitor
            ? db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE conversations
                SET operator_unread_count = operator_unread_count + 1
                WHERE id = {conversationId.Value} AND {sequence} > operator_last_read_sequence
                """,
                cancellationToken)
            : db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE conversations
                SET visitor_unread_count = visitor_unread_count + 1
                WHERE id = {conversationId.Value}
                """,
                cancellationToken);
    }
}
