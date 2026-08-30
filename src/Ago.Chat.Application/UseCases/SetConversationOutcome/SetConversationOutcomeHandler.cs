using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.SetConversationOutcome;

/// <summary>
/// `18-10`: the write half of this item - an operator recording what a conversation actually led to.
///
/// <para><b>Permission only, no per-conversation ownership check.</b> <c>CloseConversationHandler</c>
/// additionally compares <c>conversation.OperatorId</c> against the caller, because closing is a state
/// transition scoped to whoever is actually handling the conversation (that handler's own remarks).
/// This item's backlog file states only "gated on the same permission" - it does not repeat Close's
/// ownership restriction, and there is a real reason not to invent one: a conversation can be
/// transferred (`18-02`) or reassigned after close, and the operator best placed to know what it led to
/// (spoke with the visitor after the fact, saw the order come through) is not guaranteed to be whoever
/// the conversation happens to be assigned to at the moment they record it. <c>TagConversationHandler</c>
/// is the closer precedent: any operator holding the relevant permission for the site may act, the same
/// shape this handler follows.</para>
///
/// <para><b>Goes through <see cref="IConversationRepository"/>, not a lighter read-store-backed write</b>
/// (unlike <c>TagConversationHandler</c>, which never loads the aggregate at all because tags live in
/// their own table). <see cref="Conversation.Outcome"/> is a real property of the aggregate - the same
/// "a conversation's own scalar state lives on the aggregate, mapped by <c>ConversationConfiguration</c>"
/// shape <see cref="Conversation.ClosedAt"/> and <see cref="Conversation.State"/> already use, not a
/// sibling table the way `18-04`'s many-tags-per-conversation model needed one. That means this write
/// races the same row's `xmin` against an ordinary message send the way <c>CloseConversationHandler</c>
/// does, so it reuses that handler's own retry-once shape rather than surfacing a spurious `409` for a
/// routine, narrow-window collision with a message that happened to land at the same moment.</para>
/// </summary>
public sealed class SetConversationOutcomeHandler(
    IConversationRepository conversations, IPermissionChecker permissions)
{
    public async Task<Result> HandleAsync(SetConversationOutcome command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.ConversationClose, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to record conversation outcomes for this site.");
        }

        if (!Enum.TryParse<ConversationOutcome>(command.Outcome, ignoreCase: true, out var outcome)
            || !Enum.IsDefined(outcome)
            || outcome == ConversationOutcome.Unset)
        {
            return ConversationErrors.OutcomeInvalid(
                $"'{command.Outcome}' is not a recordable conversation outcome; use Converted, NotConverted or FollowUpNeeded.");
        }

        var conversation = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null || conversation.SiteId != command.SiteId)
        {
            // Wrong-tenant reads like no-such-row, the same info-hiding shape `ConversationErrors`'
            // own remarks already establish elsewhere (`TransferTargetNotEligible`, `Tag.NotFound`).
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        try
        {
            return await SetAndSaveAsync(conversation, outcome, cancellationToken);
        }
        catch (ConversationConcurrencyConflictException)
        {
            // `6-08`'s own retry-once shape, reused verbatim - see this class's own remarks on why
            // this write can race a concurrent message send on the identical row.
            var fresh = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
            if (fresh is null || fresh.SiteId != command.SiteId)
            {
                return ConversationErrors.NotFound(command.ConversationId.Value);
            }

            try
            {
                return await SetAndSaveAsync(fresh, outcome, cancellationToken);
            }
            catch (ConversationConcurrencyConflictException)
            {
                return ConversationErrors.ConcurrencyConflict(command.ConversationId.Value);
            }
        }
    }

    private async Task<Result> SetAndSaveAsync(
        Conversation conversation, ConversationOutcome outcome, CancellationToken cancellationToken)
    {
        conversation.SetOutcome(outcome);
        await conversations.SaveAsync(conversation, cancellationToken);
        return Result.Success();
    }
}
