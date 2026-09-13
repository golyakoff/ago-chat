using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.BlockVisitor;

/// <summary>
/// `23-77`: the fix for "closing blocks a conversation, and the person opens another" - an operator,
/// looking at one conversation, blocks the *visitor* behind it, indefinitely, on this site. Writes a
/// <c>visitor_restrictions</c> row with <c>ExpiresAt = null</c> (this item's own Scope: "reversible
/// and recorded... stays scoped to one site", the indefinite half of the shared mechanism `23-69`'s
/// time-windowed mute also writes to).
///
/// <para><b>Does not touch `24-10`'s own <see cref="IConversationBlockRepository"/>/<see
/// cref="Conversation.BlockedAt"/> at all</b> - both items' own "Answered" sections are explicit that
/// the new table is additive, not a repurposing, and `24-10`'s own mechanism has no console caller to
/// preserve behaviour for (`docs/backlog/24-10-*.md`'s own note: "explicitly left a console screen for
/// its own number", never built). Reusing it here - setting <c>conversations.blocked_at</c> on the one
/// conversation this action was invoked from, in addition to the new table - was considered and
/// rejected: it would silently give this new action a second, undocumented side effect on a mechanism
/// this item's own dialogue with the author settled should stay untouched, for no stated benefit
/// (`GetOperatorQueueHandler`'s own filters, plus <c>StartConversationHandler</c>'s own
/// <c>RoutingSuppressedAt</c> check for every conversation opened afterward, already cover
/// everything this item's own Scope asks for without it).</para>
///
/// <para>Gated on <see cref="Permission.ConversationBlock"/> - reused, not a new permission: this is
/// the same capability `24-10` already named and scoped Admin-only ("freezing and unfreezing are the
/// same capability exercised in either direction" - that permission's own remarks), now finally given
/// the visitor-scoped mechanism its own name always implied. The conversation itself is not closed or
/// otherwise altered by this act - blocking is independent of closing, the same "non-destructive and
/// reversible" shape `24-10` already gives its own per-conversation block.</para>
/// </summary>
public sealed class BlockVisitorHandler(
    IConversationRepository conversations, IVisitorRestrictionRepository restrictions, IPermissionChecker permissions,
    IIdGenerator idGenerator, IClock clock)
{
    public async Task<Result<BlockVisitorResult>> HandleAsync(BlockVisitor command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.OperatorId, command.SiteId, Permission.ConversationBlock, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to block visitors for this site.");
        }

        var conversation = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null || conversation.SiteId != command.SiteId)
        {
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        var now = clock.UtcNow;
        var recordId = idGenerator.NewId(now);
        await restrictions.RestrictAsync(
            command.SiteId,
            conversation.VisitorId,
            command.OperatorId,
            VisitorRestrictionKind.Block,
            expiresAt: null,
            conversation.Id,
            recordId,
            now,
            cancellationToken);

        return new BlockVisitorResult(conversation.VisitorId, now, command.OperatorId);
    }
}

/// <summary>The wire shape of a successful block - the operator who just invoked the action already
/// holds the visitor's id from the conversation on screen; this confirms the act actually applied and
/// when, the same "the caller needs to know the action landed" reason `ConversationBlockStatus`
/// already returns for `24-10`'s own pair.</summary>
public sealed record BlockVisitorResult(VisitorId VisitorId, DateTimeOffset OccurredAt, OperatorId OperatorId);
