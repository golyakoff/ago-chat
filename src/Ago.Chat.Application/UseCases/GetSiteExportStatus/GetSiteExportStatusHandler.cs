using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetSiteExportStatus;

/// <summary>
/// `16-03`: the completion poll the console drives after `RequestSiteExportHandler` returns `202` -
/// the same "no fetch endpoint existed, this item needs one" gap `16-02`'s own
/// <c>GetConversationByIdHandler</c> found and filled, for export instead of erasure.
///
/// <para>Gated by <see cref="Permission.SiteExport"/>, the same permission the trigger endpoint checks
/// - the only legitimate caller of this poll is an operator who could have requested this export in
/// the first place. Scoped by <paramref name="siteId"/> as well as the export id
/// (<see cref="IExportRequestRepository.GetAsync"/>'s own remarks): an operator who holds
/// <c>SiteExport</c> on a *different* site and happens to guess or intercept another tenant's export
/// id is refused with the same <c>404</c> as a genuinely nonexistent id, never told "this exists, you
/// just cannot see it" - the cross-tenant existence leak `16-02`'s own erasure port already refuses to
/// create.</para>
/// </summary>
public sealed class GetSiteExportStatusHandler(
    IExportRequestRepository exportRequests, IFileStorage fileStorage, IPermissionChecker permissions, SiteExportOptions options)
{
    public async Task<Result<SiteExportStatusItem>> HandleAsync(
        GetSiteExportStatus query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteExport, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to view this site's export.");
        }

        var record = await exportRequests.GetAsync(query.ExportId, query.SiteId, cancellationToken);
        if (record is null)
        {
            return ConversationErrors.ExportNotFound(query.ExportId);
        }

        // Minted fresh on every poll, never stored - SiteExportOptions.DownloadUrlLifetime's own
        // remarks on why this stays deliberately uncached.
        Uri? downloadUrl = null;
        if (record is { Status: ExportStatus.Ready, ObjectKey: { } objectKey })
        {
            downloadUrl = await fileStorage.CreateDownloadUrlAsync(
                new ObjectKey(objectKey), options.DownloadUrlLifetime, cancellationToken);
        }

        return new SiteExportStatusItem(
            record.Id, record.Status, record.RequestedAt, record.CompletedAt, downloadUrl, record.FailureReason);
    }
}
