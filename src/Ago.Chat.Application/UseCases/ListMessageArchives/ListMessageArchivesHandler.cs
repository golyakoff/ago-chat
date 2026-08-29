using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.ListMessageArchives;

/// <summary>
/// `13-06`: "a tenant can request and receive an archived period" - the discovery half. Unlike `16-03`'s
/// export (built on demand, so the console needs a request-then-poll pair), a retention archive already
/// exists by the time any operator could ask for it - <c>MessageArchiveJob</c> writes it well before
/// the corresponding partition is ever a drop candidate - so there is nothing to enqueue here, only a
/// read. Gated by <see cref="Permission.SiteExport"/>: retrieving a tenant's own historical data is the
/// same class of action `16-03`'s own permission already covers, and inventing a second permission for
/// "read your own archived messages" would split one capability into two for no access-control
/// distinction anyone has asked for.
/// </summary>
public sealed class ListMessageArchivesHandler(IMessageArchiveRepository archives, IPermissionChecker permissions)
{
    public async Task<Result<IReadOnlyList<MessageArchiveRecord>>> HandleAsync(
        ListMessageArchives query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(query.RequestedBy, query.SiteId, Permission.SiteExport, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to view this site's message archives.");
        }

        var records = await archives.ListForSiteAsync(query.SiteId, cancellationToken);
        return Result<IReadOnlyList<MessageArchiveRecord>>.Success(records);
    }
}
