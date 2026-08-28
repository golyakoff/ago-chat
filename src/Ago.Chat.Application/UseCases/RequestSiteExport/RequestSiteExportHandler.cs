using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RequestSiteExport;

/// <summary>
/// `16-03`: triggers an asynchronous tenant-data export. Inserts one <c>Pending</c>
/// <c>export_requests</c> row and returns immediately - no packaging work here, the same
/// "deletion/export is a job, not a request handler" shape `16-02`'s
/// <c>RequestSiteErasureHandler</c> already established, for the identical reason: streaming a
/// tenant's full history and uploading the result touches Postgres and object storage across what can
/// be a genuinely long-running call, and a synchronous HTTP request a timeout can tear in half is
/// exactly the shape that must not happen here. <c>Ago.Chat.Worker</c>'s <c>SiteExportJob</c> is what
/// actually builds and uploads the archive, off its own timer.
///
/// <para>Gated by <see cref="Permission.SiteExport"/> - see that permission's own remarks for why
/// export earns a permission distinct from <see cref="Permission.SiteConfigure"/> and
/// <see cref="Permission.SiteErase"/>.</para>
///
/// <para><b>Permission checked before the rate limit, not after.</b> <c>RegisterSiteHandler</c> and
/// <c>CreateAttachmentHandler</c> both rate-limit first ("a bad caller still costs them a token, never
/// costs us a query") - reversed here deliberately, because this bucket is keyed per *site*, not per
/// caller (<see cref="SiteExportRateLimitOptions"/>'s own remarks). Checking permission first means an
/// operator with no export permission on this site can never spend a share of the tenant's own shared
/// budget finding that out - the ordering those other handlers use would let an unauthorised caller
/// degrade a legitimate tenant's own export allowance, which ordering permission-first avoids.</para>
/// </summary>
public sealed class RequestSiteExportHandler(
    IExportRequestRepository exportRequests,
    IRateLimiter rateLimiter,
    IPermissionChecker permissions,
    SiteExportRateLimitOptions rateLimitOptions,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<Guid>> HandleAsync(RequestSiteExport command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.SiteExport, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to export this site's data.");
        }

        var limit = await rateLimiter.CheckAsync(
            new RateLimitKey($"site-export:site:{command.SiteId.Value}"),
            new RateLimitRule(rateLimitOptions.PerSiteCapacity, rateLimitOptions.PerSiteRefillPerSecond),
            cancellationToken);
        if (!limit.Allowed)
        {
            return ConversationErrors.ExportRateLimited(limit.RetryAfter);
        }

        var now = clock.UtcNow;
        var exportId = idGenerator.NewId(now);

        var created = await exportRequests.CreateAsync(exportId, command.SiteId, command.RequestedBy, now, cancellationToken);
        if (!created)
        {
            return ConversationErrors.SiteNotFound(command.SiteId.Value);
        }

        return exportId;
    }
}
