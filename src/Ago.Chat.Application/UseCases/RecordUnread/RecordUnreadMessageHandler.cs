using Ago.Chat.Application.Abstractions;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RecordUnread;

/// <summary>
/// 2-05's first real consumer of `MessageAccepted`: increments whichever side's unread count the
/// message affects, exactly once per message regardless of redelivery.
///
/// The increment is staged on the tracked <see cref="Domain.Conversation"/> but never saved by this
/// handler - <see cref="IInboxChecker.TryRecordAndSaveAsync"/> performs the one `SaveChangesAsync`
/// that commits both the increment and the inbox dedup row together (adr/0017). Calling
/// `IConversationRepository.SaveAsync` here as well would run a second, separate `SaveChangesAsync`
/// and split the two into different transactions - exactly the mistake adr/0017 warns a consumer can
/// make silently, so this handler deliberately never calls it.
///
/// Naturally idempotent even if the inbox check were skipped entirely: a redelivered message reloads
/// the conversation, re-applies the same increment in memory, and only the save's outcome (a unique
/// violation on `(message_id, consumer)`) decides whether it actually lands - not a prior read of
/// "have I seen this." Safe under concurrent delivery of *different* messages for the same
/// conversation too: `Conversation` is mapped with Postgres's `xmin` as an optimistic-concurrency
/// token, so a losing concurrent save throws `DbUpdateConcurrencyException` (a `DbUpdateException`
/// subtype `EfInboxChecker` does not treat as a duplicate) rather than silently overwriting the
/// other side's increment - it propagates, the broker retries, and a later attempt reloads the
/// fresh count.
/// </summary>
public sealed class RecordUnreadMessageHandler(IConversationRepository conversations, IInboxChecker inbox)
{
    public const string ConsumerName = "unread-counter";

    /// <summary>Returns whether this was the first delivery (increment applied) or a duplicate
    /// (nothing changed) - purely for the caller's own logging, never a reason to Nack either way.</summary>
    public async Task<Result<bool>> HandleAsync(RecordUnreadMessage command, CancellationToken cancellationToken)
    {
        var conversation = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        conversation.IncrementUnreadCount(command.AuthorKind);

        var isFirstDelivery = await inbox.TryRecordAndSaveAsync(command.MessageId, ConsumerName, cancellationToken);
        return isFirstDelivery;
    }
}
