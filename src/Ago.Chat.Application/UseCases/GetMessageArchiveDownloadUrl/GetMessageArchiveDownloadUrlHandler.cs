using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetMessageArchiveDownloadUrl;

/// <summary>
/// `13-06`: "a tenant can request and receive an archived period" - the retrieval half. Mints a
/// presigned download URL against the object <c>MessageArchiveJob</c> already wrote - never a restore
/// into the live product (`adr/0031`'s own explicit rejection of that, with reasons). The same
/// deliberately-uncached-per-poll shape <c>GetSiteExportStatusHandler</c> already establishes for the
/// identical reason: a low-frequency, one-per-(site, period) read gains nothing from a cache entry that
/// would only ever be read back once before its own TTL made it stale.
/// </summary>
public sealed class GetMessageArchiveDownloadUrlHandler(
    IMessageArchiveRepository archives, IFileStorage fileStorage, IPermissionChecker permissions, MessageArchiveOptions options)
{
    public async Task<Result<Uri>> HandleAsync(GetMessageArchiveDownloadUrl query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(query.RequestedBy, query.SiteId, Permission.SiteExport, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to view this site's message archives.");
        }

        var record = await archives.GetAsync(query.SiteId, query.RetentionClass, query.PeriodStart, cancellationToken);
        if (record is null)
        {
            return ConversationErrors.MessageArchiveNotFound(query.RetentionClass.Value, query.PeriodStart);
        }

        return await fileStorage.CreateDownloadUrlAsync(new ObjectKey(record.ObjectKey), options.DownloadUrlLifetime, cancellationToken);
    }
}
