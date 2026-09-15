using Ago.Chat.Application.Abstractions;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RecordUnread;

/// <summary>
/// 2-05's first real consumer of `MessageAccepted`: increments whichever side's unread count the
/// message affects, exactly once per message regardless of redelivery.
///
/// `25-109`: the increment is no longer staged on the tracked <see cref="Domain.Conversation"/> and
/// saved alongside the inbox row by one shared <see cref="IInboxChecker.TryRecordAndSaveAsync"/> call -
/// that EF load-mutate-save staked this row's own `xmin` against every other writer of the same row for
/// the whole request, which is exactly what let this consumer collide with
/// <c>Ago.Chat.Infrastructure.Postgres.Pipeline.MessageBatchWriter</c>'s own per-flush
/// `SaveChangesAsync` under load (`25-109`'s own root-cause finding: this consumer fires on every
/// single message, far more often than any other cross-process writer of `conversations`, so it was the
/// dominant source of `DbUpdateConcurrencyException` bursts there). <see cref="IUnreadCounterStore"/>
/// now applies the identical effect (see its own remarks) as a standalone, atomic `UPDATE` that never
/// loads or mutates the aggregate at all - see <see cref="Domain.Conversation.IncrementUnreadCount"/>'s
/// own remarks for why that column split is safe.
///
/// <para><b>Atomicity with the inbox-dedup row is now explicit, not implicit.</b> The two writes no
/// longer share one `SaveChangesAsync` call by construction - a raw `ExecuteSqlInterpolatedAsync` and a
/// later `SaveChangesAsync` on the same `DbContext` are two separate, independently-committing
/// operations unless a transaction spans both (`adr/0017` requires exactly this atomicity: a redelivery
/// that finds the inbox row already recorded must leave the counter exactly where the first delivery
/// left it, never incrementing twice and never losing the increment). This handler opens that
/// transaction itself via <see cref="IUnitOfWork"/> - `18-02`'s own port for "more than one write
/// commits or rolls back as a single unit," the identical seam <c>TransferConversationHandler</c>
/// already uses, and the reason a plain `DbContext.Database.BeginTransactionAsync` cannot appear here
/// directly (CLAUDE.md rule 2). <see cref="IInboxChecker.TryRecordAndSaveAsync"/>'s own
/// `SaveChangesAsync` joins that ambient transaction automatically (`OperatorCapacityStore`'s own
/// remarks describe the same EF mechanism) - committed only when it reports the first delivery, left to
/// roll back (via `await using`, no explicit `RollbackAsync` call - <see cref="IUnitOfWork"/>'s own
/// remarks on why) on a duplicate, which undoes the counter increment right along with the inbox row
/// that was never actually added.
///
/// Naturally idempotent under at-least-once delivery for the identical reason as before, restated for
/// the new write: a redelivered message re-issues the same conditional `UPDATE`, and only the
/// transaction's own outcome - commit on first delivery, rollback on a duplicate - decides whether it
/// actually lands, never a prior read of "have I seen this."
///
/// `5-15`: still composes with the *other* writer this counter has - `MarkConversationReadHandler`,
/// running in `Ago.Chat.Api`, which still writes through the aggregate (out of this item's scope; that
/// path's own writes go through `IConversationRepository.SaveAsync`, already correctly handled). The
/// composition is actually strengthened by this change, not merely preserved: the raw `UPDATE`'s
/// `@sequence > operator_last_read_sequence` condition reads the row's current value under Postgres's
/// own row lock at the instant it runs, rather than a value read earlier and compared via `xmin` - a
/// genuine compare-and-set, with no reload-and-retry needed to get the right answer under a concurrent
/// mark-read.
/// </summary>
public sealed class RecordUnreadMessageHandler(
    IConversationRepository conversations, IUnreadCounterStore unreadCounter, IUnitOfWork unitOfWork, IInboxChecker inbox)
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

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        await unreadCounter.IncrementAsync(command.ConversationId, command.AuthorKind, command.Sequence, cancellationToken);

        var isFirstDelivery = await inbox.TryRecordAndSaveAsync(command.MessageId, ConsumerName, cancellationToken);
        if (isFirstDelivery)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        // A duplicate (isFirstDelivery == false): transaction is disposed, unsaved, right below -
        // rolling back the increment above along with the inbox insert IInboxChecker already found it
        // could not add, so a redelivery neither double-counts nor silently loses the message.
        return isFirstDelivery;
    }
}
