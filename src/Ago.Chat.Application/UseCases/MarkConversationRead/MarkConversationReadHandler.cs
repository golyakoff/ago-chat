using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.MarkConversationRead;

/// <summary>
/// `5-15`: the write that was missing since `2-05` - <c>OperatorUnreadCount</c> had exactly one
/// writer (<see cref="Conversation.IncrementUnreadCount"/>) and nothing that ever brought it down.
///
/// <para><b>Permission.</b> <see cref="Permission.ConversationRead"/>, not a new
/// <c>conversation:mark_read</c>: marking read is a side effect of reading, and adr/0016's granular
/// permissions exist so a role can be denied an *action* someone might reasonably want withheld -
/// "may view conversations but may not admit to having viewed them" is not one. On top of that comes
/// the per-conversation check <see cref="Conversation.MarkReadByOperator"/> makes itself: RBAC answers
/// "may this operator act at all", the aggregate answers "on this one" - the same split
/// <c>CloseConversationHandler</c> and <c>ConfirmAttachmentHandler</c> already draw.</para>
///
/// <para><b>No outbox row, no domain event.</b> Nothing downstream reacts to a conversation being
/// read - `2-05` already decided that an unread count changing is not an integration event, and
/// per-message read receipts (telling the *visitor* an operator has read them) are explicitly out of
/// `5-15`'s scope. So this handler takes no <c>IOutboxWriter</c>: rule 4 governs a state change that
/// *has* an integration event, and inventing one nothing consumes would be worse than not having it.</para>
///
/// <para><b>Retry once on a concurrency conflict, then report it.</b> `6-08` established this shape
/// for conversation writes and it applies unchanged here, for a stronger reason than it had there:
/// the racing writer is usually the very thing being marked read (a visitor message bumping this
/// row's `xmin`, or `2-05`'s own consumer incrementing the count), so a conflict is the expected
/// case under load, not an exotic one. The retry is safe because
/// <see cref="Conversation.MarkReadByOperator"/> re-derives its answer from whatever is on disk on
/// reload rather than replaying a decision made against the stale copy - clearing *up to a sequence*
/// is what makes that true; an unconditional zero would re-apply "zero" over the concurrent
/// increment. Doing nothing at all was the alternative (`5-15` names it as defensible, since the
/// next open re-issues the call) and was rejected: the console marks read on open and then leaves
/// the conversation on screen for minutes, so "the next open" can be a long way off, and the badge
/// would sit visibly wrong the whole time for a race the server can resolve in one extra round trip.
/// A second conflict inside that already-narrow window becomes
/// <see cref="ConversationErrors.ConcurrencyConflict"/> (`409`) rather than an unbounded loop - and
/// unlike close/assign, a `409` here is genuinely harmless: the console retries on the next open, and
/// nothing about the conversation is half-applied.</para>
/// </summary>
public sealed class MarkConversationReadHandler(
    IConversationRepository conversations,
    IPermissionChecker permissions)
{
    public async Task<Result<MarkConversationReadResult>> HandleAsync(
        MarkConversationRead command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.OperatorId, command.SiteId, Permission.ConversationRead, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to read conversations for this site.");
        }

        try
        {
            return await MarkAndSaveAsync(command, cancellationToken);
        }
        catch (ConversationConcurrencyConflictException)
        {
            try
            {
                return await MarkAndSaveAsync(command, cancellationToken);
            }
            catch (ConversationConcurrencyConflictException)
            {
                return ConversationErrors.ConcurrencyConflict(command.ConversationId.Value);
            }
        }
    }

    /// <summary>One attempt, reload included - unlike `6-08`'s handlers this re-reads inside the
    /// attempt rather than taking a conversation loaded by the caller, because there is nothing to
    /// carry over between attempts: the whole decision (how much of the newly-read range was actually
    /// counted) is a function of the row as it stands right now.</summary>
    private async Task<Result<MarkConversationReadResult>> MarkAndSaveAsync(
        MarkConversationRead command, CancellationToken cancellationToken)
    {
        var conversation = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        bool changed;
        try
        {
            changed = conversation.MarkReadByOperator(command.OperatorId, command.UpToSequence);
        }
        catch (ConversationParticipantMismatchException)
        {
            // Deliberately not echoing the domain exception's message: it names the assigned operator's
            // conversation membership, and the caller asking is by definition not a party to it.
            return ConversationErrors.Forbidden("This operator is not assigned to this conversation.");
        }

        // `5-15`'s "marking an already-read conversation read is a no-op": no save at all, not a save
        // that happens to write the same values. A pointless UPDATE would still bump the row's `xmin`
        // and make a genuinely concurrent writer lose - the console calls this on every open, so that
        // would be a self-inflicted conflict on the hottest path this write has.
        if (changed)
        {
            await conversations.SaveAsync(conversation, cancellationToken);
        }

        return new MarkConversationReadResult(
            conversation.OperatorUnreadCount, conversation.OperatorLastReadSequence);
    }
}
