using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetAllConversationsForSite;

/// <summary>
/// `5-08`: the admin/supervisor role's distinguishing feature per `authorization.md` - "sees every
/// conversation for a site (not just its own assigned ones)". Gated on
/// <see cref="Permission.SiteConfigure"/>, not <see cref="Permission.ConversationRead"/> -
/// `ConversationRead` is what every ordinary operator already holds and only ever unlocks their own
/// assigned/waiting-queue view (`GetOperatorQueueHandler`); this handler intentionally does not
/// extend that check to be site-wide, since doing so would let any ordinary operator read this list
/// too, defeating the reason `authorization.md` named a separate admin role in the first place.
/// `SiteConfigure` over `SiteManageOperators` because this is a site-oversight read, not an
/// operator-management action - the latter is reserved for a future role-assignment surface this
/// item deliberately does not build (see this item's own commit-prep notes on that decision).
/// </summary>
public sealed class GetAllConversationsForSiteHandler(
    IConversationReadStore readStore, IPermissionChecker permissions)
{
    public async Task<Result<AllConversationsForSiteResponse>> HandleAsync(
        GetAllConversationsForSite query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to view every conversation for this site.");
        }

        var page = await readStore.GetAllForSiteAsync(query.SiteId, query.BeforeId, query.PageSize, cancellationToken);

        return new AllConversationsForSiteResponse(page.Conversations.Select(ToSummary).ToList(), page.NextBeforeId);
    }

    private static ConversationSummaryDto ToSummary(ConversationSummaryItem item) => new(
        item.Id.Value, item.VisitorId.Value, item.State, item.CreatedAt, item.OperatorUnreadCount, item.OperatorId?.Value);
}
