using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetConversationOutcome;

/// <summary>`18-10`: reuses `IConversationReadStore.GetByIdAsync` - the identical site-scoped point
/// lookup `GetConversationTagsHandler` already uses for its own existence/tenant check - rather than a
/// new read-store method, since `ConversationSummaryItem.Outcome` (additive, this item's own change)
/// already carries the one field this handler needs.
///
/// <para>Returns the raw CLR member name, not <see cref="ConversationOutcome"/> itself -
/// <see cref="ConversationSummaryItem"/>'s own remarks establish that this read model is a plain
/// projection, never a domain type, the same reason its sibling <c>State</c> field is <see cref="string"/>
/// rather than <see cref="Domain.ConversationState"/>.</para>
/// </summary>
public sealed class GetConversationOutcomeHandler(
    IConversationReadStore readStore, IPermissionChecker permissions)
{
    public async Task<Result<string>> HandleAsync(
        GetConversationOutcome query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.ConversationRead, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to read conversations for this site.");
        }

        var conversation = await readStore.GetByIdAsync(query.ConversationId, query.SiteId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(query.ConversationId.Value);
        }

        return conversation.Outcome;
    }
}
