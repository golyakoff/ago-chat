using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.BlockConversation;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.UnblockConversation;

/// <summary>
/// `24-10`: the reverse of <see cref="Application.UseCases.BlockConversation.BlockConversationHandler"/> -
/// same permission, same scoping, same one-statement atomic write, restated for the opposite direction.
/// Reuses <see cref="ConversationBlockStatus"/> from that handler's own file rather than a second,
/// near-identical response type: both actions answer the identical question ("what is this
/// conversation's block state now"), and <see cref="ConversationBlockStatus.OccurredAt"/>/
/// <see cref="ConversationBlockStatus.OperatorId"/> read equally well as "when/who blocked" and
/// "when/who unblocked" depending on which endpoint the caller just invoked.
///
/// <para><b>"Unblocking restores exactly the prior state" (this item's own Done-when) is a claim about
/// <see cref="Domain.Conversation.BlockedAt"/>/<see cref="Domain.Conversation.BlockedBy"/> going back to
/// <see langword="null"/>, and about every read this item hides a blocked conversation from becoming
/// reachable again</b> - it is not a claim this handler needs to do anything special to make true: those
/// two columns are the only state <see cref="IConversationBlockRepository.BlockAsync"/> ever changed, so
/// clearing them here is already the complete reversal. Nothing else on the conversation (messages sent
/// while blocked, its own <see cref="Domain.ConversationState"/>, unread counts) was ever touched by
/// blocking in the first place - see <see cref="IConversationBlockRepository"/>'s own remarks on why the
/// write is exactly these two columns and nothing more.</para>
/// </summary>
public sealed class UnblockConversationHandler(
    IConversationBlockRepository blockRepository, IPermissionChecker permissions, IIdGenerator idGenerator, IClock clock)
{
    public async Task<Result<ConversationBlockStatus>> HandleAsync(UnblockConversation command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.ConversationBlock, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to unblock conversations for this site.");
        }

        var now = clock.UtcNow;
        var blockRecordId = idGenerator.NewId(now);

        var outcome = await blockRepository.UnblockAsync(
            command.ConversationId, command.SiteId, command.RequestedBy, blockRecordId, now, cancellationToken);

        return outcome switch
        {
            ConversationBlockOutcome.NotFound => ConversationErrors.NotFound(command.ConversationId.Value),
            ConversationBlockOutcome.AlreadyInState => ConversationErrors.ConversationNotBlocked(command.ConversationId.Value),
            ConversationBlockOutcome.Applied => new ConversationBlockStatus(command.ConversationId, now, command.RequestedBy),
            _ => throw new InvalidOperationException($"Unhandled {nameof(ConversationBlockOutcome)}: {outcome}."),
        };
    }
}
