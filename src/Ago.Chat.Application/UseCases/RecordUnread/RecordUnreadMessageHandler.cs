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
///
/// `5-15`: that same reload-on-conflict is now also what makes this consumer compose with the
/// *other* writer this counter has gained - <c>MarkConversationReadHandler</c>, running in
/// <c>Ago.Chat.Api</c>. The increment is no longer unconditional: <see cref="Domain.Conversation.IncrementUnreadCount"/>
/// skips a message at or below the operator's read watermark, so a mark-read that commits first
/// correctly swallows this increment, and a mark-read that commits second correctly leaves it
/// standing. Note that the inbox row is still written either way - "the operator had already read
/// it" is not "this delivery did not happen", and letting a skipped increment go unrecorded would
/// hand a later redelivery a second chance to apply it against a moved watermark.
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

        conversation.IncrementUnreadCount(command.AuthorKind, command.Sequence);

        var isFirstDelivery = await inbox.TryRecordAndSaveAsync(command.MessageId, ConsumerName, cancellationToken);
        return isFirstDelivery;
    }
}
