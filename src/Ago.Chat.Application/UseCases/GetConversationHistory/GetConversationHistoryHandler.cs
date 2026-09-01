using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetConversationHistory;

/// <summary>
/// One handler, two entry points: unlike <c>SendMessage</c>, the two callers do not call different
/// <see cref="Domain.Conversation"/> methods - they differ only in *how access is checked*, and share
/// everything after that, so splitting into two classes would just duplicate the fetch.
/// </summary>
public sealed class GetConversationHistoryHandler(
    IConversationRepository conversations,
    IConversationReadStore readStore,
    IPermissionChecker permissions)
{
    public async Task<Result<ConversationHistoryPage>> HandleAsVisitorAsync(
        GetConversationHistoryAsVisitor query, CancellationToken cancellationToken)
    {
        var conversation = await conversations.GetByIdAsync(query.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(query.ConversationId.Value);
        }

        if (conversation.VisitorId != query.RequestedBy)
        {
            return ConversationErrors.Forbidden("This visitor is not a participant of this conversation.");
        }

        var page = await readStore.GetHistoryAsync(
            query.ConversationId, conversation.SiteId, query.BeforeSequence, query.PageSize, cancellationToken);
        return page;
    }

    public async Task<Result<ConversationHistoryPage>> HandleAsOperatorAsync(
        GetConversationHistoryAsOperator query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.ConversationRead, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to read conversations for this site.");
        }

        var conversation = await conversations.GetByIdAsync(query.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(query.ConversationId.Value);
        }

        if (conversation.OperatorId != query.RequestedBy)
        {
            return ConversationErrors.Forbidden("This operator is not assigned to this conversation.");
        }

        var page = await readStore.GetHistoryAsync(
            query.ConversationId, conversation.SiteId, query.BeforeSequence, query.PageSize, cancellationToken);
        return page;
    }

    /// <summary>
    /// `3-03`: the same access checks as <see cref="HandleAsVisitorAsync"/>, a different cursor
    /// direction on the same read store - not a new handler class, per realtime.md's Client protocol
    /// section (the delta since a known sequence is the same query as the history page, read forward).
    /// </summary>
    public async Task<Result<IReadOnlyList<MessageHistoryItem>>> HandleDeltaAsVisitorAsync(
        GetConversationDeltaAsVisitor query, CancellationToken cancellationToken)
    {
        var conversation = await conversations.GetByIdAsync(query.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(query.ConversationId.Value);
        }

        if (conversation.VisitorId != query.RequestedBy)
        {
            return ConversationErrors.Forbidden("This visitor is not a participant of this conversation.");
        }

        var delta = await readStore.GetDeltaAsync(query.ConversationId, conversation.SiteId, query.AfterSequence, cancellationToken);
        return Result<IReadOnlyList<MessageHistoryItem>>.Success(delta);
    }

    /// <summary>Operator-side equivalent of <see cref="HandleDeltaAsVisitorAsync"/> - see its remarks.</summary>
    public async Task<Result<IReadOnlyList<MessageHistoryItem>>> HandleDeltaAsOperatorAsync(
        GetConversationDeltaAsOperator query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.ConversationRead, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to read conversations for this site.");
        }

        var conversation = await conversations.GetByIdAsync(query.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(query.ConversationId.Value);
        }

        if (conversation.OperatorId != query.RequestedBy)
        {
            return ConversationErrors.Forbidden("This operator is not assigned to this conversation.");
        }

        var delta = await readStore.GetDeltaAsync(query.ConversationId, conversation.SiteId, query.AfterSequence, cancellationToken);
        return Result<IReadOnlyList<MessageHistoryItem>>.Success(delta);
    }
}
