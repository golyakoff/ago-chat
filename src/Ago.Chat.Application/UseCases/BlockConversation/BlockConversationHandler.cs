using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.BlockConversation;

/// <summary>
/// `24-10`: a controller (the tenant, acting through an operator) suspends processing of one
/// conversation's data - the mechanism `docs/backlog/24-10-*.md` exists to build, "the statutory
/// operation list... includes blocking as a distinct operation from destruction."
///
/// <para>Gated by <see cref="Permission.ConversationBlock"/> - deliberately not
/// <see cref="Permission.ConversationErase"/> or <see cref="Permission.ConversationClose"/>. See
/// <see cref="Permission.ConversationBlock"/>'s own remarks for why this is its own permission rather
/// than a reuse.</para>
///
/// <para>No aggregate load: <see cref="IConversationBlockRepository.BlockAsync"/> is raw SQL end to
/// end, scoped by <c>(conversationId, siteId)</c> the same way
/// <see cref="Application.UseCases.RequestConversationErasure.RequestConversationErasureHandler"/>'s own
/// <see cref="IErasureRequestRepository"/> call is - see that type's own remarks and
/// <see cref="IConversationBlockRepository"/>'s for why routing this through
/// <see cref="IConversationRepository"/> would cost more than it buys.</para>
/// </summary>
public sealed class BlockConversationHandler(
    IConversationBlockRepository blockRepository, IPermissionChecker permissions, IIdGenerator idGenerator, IClock clock)
{
    public async Task<Result<ConversationBlockStatus>> HandleAsync(BlockConversation command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.ConversationBlock, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to block conversations for this site.");
        }

        var now = clock.UtcNow;
        var blockRecordId = idGenerator.NewId(now);

        var outcome = await blockRepository.BlockAsync(
            command.ConversationId, command.SiteId, command.RequestedBy, blockRecordId, now, cancellationToken);

        return outcome switch
        {
            ConversationBlockOutcome.NotFound => ConversationErrors.NotFound(command.ConversationId.Value),
            ConversationBlockOutcome.AlreadyInState => ConversationErrors.ConversationAlreadyBlocked(command.ConversationId.Value),
            ConversationBlockOutcome.Applied => new ConversationBlockStatus(command.ConversationId, now, command.RequestedBy),
            _ => throw new InvalidOperationException($"Unhandled {nameof(ConversationBlockOutcome)}: {outcome}."),
        };
    }
}

/// <summary>The block/unblock actions' own success response - deliberately not
/// <see cref="ConversationSummaryItem"/> (that record answers "what does this conversation look like",
/// and a blocked conversation is meant to stop being reachable through the reads that would ask that
/// question - see this item's own commit-prep notes on why <see cref="Abstractions.IConversationReadStore.GetByIdAsync"/>
/// now excludes a blocked row rather than showing it with a flag). This is the one place a blocked
/// conversation's status is still handed back on purpose: the operator who just invoked the action
/// already holds the id, and needs to know the action actually applied.</summary>
public sealed record ConversationBlockStatus(ConversationId ConversationId, DateTimeOffset OccurredAt, OperatorId OperatorId);
