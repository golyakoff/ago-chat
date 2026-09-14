using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.ListSiteAttachments;

/// <summary>`23-80`'s "largest conversations, not only largest files" view - its own small handler
/// rather than a third branch on <see cref="ListSiteAttachmentsHandler"/>, because its result shape
/// (one row per conversation, not per attachment) is genuinely a different read, sharing only the
/// read store and the permission gate.</summary>
public sealed class GetLargestConversationsForSiteHandler(ISiteAttachmentListReadStore reads, IPermissionChecker permissions)
{
    internal const int DefaultLimit = 10;

    internal const int MaxLimit = 50;

    public async Task<Result<IReadOnlyList<LargestConversationItem>>> HandleAsync(
        GetLargestConversationsForSite query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to read this site's attachments.");
        }

        var limit = Math.Clamp(query.Limit ?? DefaultLimit, 1, MaxLimit);

        var items = await reads.ListLargestConversationsAsync(query.SiteId, limit, cancellationToken);
        return Result<IReadOnlyList<LargestConversationItem>>.Success(items);
    }
}
