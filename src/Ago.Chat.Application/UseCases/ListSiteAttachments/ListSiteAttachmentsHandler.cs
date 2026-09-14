using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.ListSiteAttachments;

/// <summary>
/// `23-80`: "Администрирование → Хранилище"'s own list. Gated on <see cref="Permission.SiteConfigure"/>
/// - the same choice <c>GetAccessRecordsForSiteHandler</c> made for the identical reason (see that
/// handler's own remarks): this is an ordinary operator acting on their own site's own data, so
/// <c>adr/0016</c>'s RBAC model is the correct check, not a platform-owner-only one. The item's own
/// words - "it is `site:configure`-shaped, not operator-shaped" - are what pick this permission over
/// <see cref="Permission.AttachmentDelete"/> (the one `5-08`'s single-attachment moderation delete
/// uses): a tenant browsing their own storage is reconfiguring their own account, not moderating one
/// conversation's content.
/// </summary>
public sealed class ListSiteAttachmentsHandler(ISiteAttachmentListReadStore reads, IPermissionChecker permissions)
{
    internal const int DefaultLimit = 50;

    internal const int MaxLimit = 200;

    public async Task<Result<AttachmentListPage>> HandleAsync(ListSiteAttachments query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to read this site's attachments.");
        }

        var limit = Math.Clamp(query.Limit ?? DefaultLimit, 1, MaxLimit);

        var page = await reads.ListAsync(query.SiteId, query.Sort, query.Filter, query.Cursor, limit, cancellationToken);
        return page;
    }
}
