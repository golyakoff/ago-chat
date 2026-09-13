using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetSiteExportStatus;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetSiteExportHistory;

/// <summary>
/// This item's own console screen: "a table of every past export request for the site." A separate
/// query from <see cref="Ago.Chat.Application.UseCases.GetSiteExportStatus.GetSiteExportStatusHandler"/>
/// rather than that one widened to accept an optional id and return either one item or a list - the
/// same "one handler answers one question" shape this codebase already keeps
/// <c>GetAccessRecordsForSiteHandler</c>/<c>GetContactRevealsForSiteHandler</c> separate from any
/// single-item read they might otherwise have been folded into: a poll-one-export caller and a
/// list-every-export caller have different callers, different permission-failure semantics worth
/// stating once each, and gluing them together would make one method's signature describe two
/// questions.
///
/// <para>Gated by <see cref="Permission.SiteExport"/>, the same permission the trigger and single-item
/// poll both check - the only legitimate caller of "every export this site has ever requested" is an
/// operator who could have requested one themselves.</para>
///
/// <para><b>Download URL, minted the same way the single-item poll already does - not a second
/// mechanism.</b> Each <c>Ready</c> row gets a fresh presigned URL from
/// <see cref="IFileStorage.CreateDownloadUrlAsync"/>, using the same
/// <see cref="SiteExportOptions.DownloadUrlLifetime"/> the poll endpoint reads
/// (<see cref="GetSiteExportStatusHandler"/>'s own remarks on why that URL is minted fresh, never
/// stored). A site's own export history is short (this item's own repository remarks), so minting one
/// presigned URL per <c>Ready</c> row in the list costs at most a handful of calls, not a page of
/// them.</para>
///
/// <para><b><see cref="SiteExportHistoryItem.ExpiresAt"/> reads
/// <see cref="SiteExportPruneJobOptions.RetentionWindow"/> - the identical config value
/// <c>SiteExportPruneJob</c> itself binds from, never a second constant</b> (that class's own remarks
/// state why one shared class, not two independently-configurable numbers, is what keeps this true).
/// </para>
/// </summary>
public sealed class GetSiteExportHistoryHandler(
    IExportRequestRepository exportRequests,
    IFileStorage fileStorage,
    IPermissionChecker permissions,
    SiteExportOptions downloadUrlOptions,
    SiteExportPruneJobOptions pruneOptions)
{
    public async Task<Result<IReadOnlyList<SiteExportHistoryItem>>> HandleAsync(
        GetSiteExportHistory query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteExport, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to view this site's exports.");
        }

        var records = await exportRequests.ListForSiteAsync(query.SiteId, cancellationToken);

        var items = new List<SiteExportHistoryItem>(records.Count);
        foreach (var record in records)
        {
            Uri? downloadUrl = null;
            DateTimeOffset? expiresAt = null;

            if (record is { Status: ExportStatus.Ready, ObjectKey: { } objectKey, CompletedAt: { } completedAt })
            {
                downloadUrl = await fileStorage.CreateDownloadUrlAsync(
                    new ObjectKey(objectKey), downloadUrlOptions.DownloadUrlLifetime, cancellationToken);
                expiresAt = completedAt + pruneOptions.RetentionWindow;
            }

            items.Add(new SiteExportHistoryItem(
                record.Id, record.Status, record.RequestedAt, record.CompletedAt, downloadUrl, expiresAt, record.FailureReason));
        }

        // `Result<T>.Success(...)` explicitly, not `return items;` - the same "an interface-typed T
        // needs the named factory, not the implicit operator" convention every other
        // `Result<IReadOnlyList<...>>` handler in this codebase follows
        // (`GetTenantAgreementsForSiteHandler`'s own remarks: a user-defined implicit conversion cannot
        // have an interface as its source or target type).
        return Result<IReadOnlyList<SiteExportHistoryItem>>.Success(items);
    }
}
