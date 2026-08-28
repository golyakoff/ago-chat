using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `16-03`: the request/status half of tenant export - creates the row `RequestSiteExportHandler`
/// stamps and answers the poll `GetSiteExportStatusHandler` serves. `Ago.Chat.Worker`'s
/// <c>SiteExportJob</c> is what actually builds and uploads the archive, off its own timer, the same
/// "no deletion work in the handler" shape `16-02`'s <see cref="IErasureRequestRepository"/> already
/// established for erasure - here it is "no packaging work in the handler."
///
/// <para><b>Its own port, not a method added to <see cref="IErasureRequestRepository"/>.</b> The two
/// share a shape (stamp a request, let a background job resolve it) but not a lifecycle: erasure's
/// request is a single nullable timestamp with no failure state and no artifact to hand back, while an
/// export produces a retrievable result with three states and an object key once ready. Widening
/// <see cref="IErasureRequestRepository"/> to carry a status enum and an object key it does not need
/// for erasure would make that port answer two questions instead of one - the same reasoning that kept
/// <c>SiteErase</c>/<c>ConversationErase</c> as two permissions rather than one applies to these two
/// ports.</para>
///
/// <para>Raw Npgsql in its implementation, not EF - there is no domain aggregate for an export
/// request to load-mutate-save through (unlike <see cref="Ago.Chat.Domain.Site"/>/
/// <see cref="Ago.Chat.Domain.Conversation"/>, which <see cref="IErasureRequestRepository"/>
/// deliberately reaches around), so this is simpler than that port's own justification, not a
/// departure from it: a request/status row with no business invariants beyond "one site, one
/// timeline" has nothing for an aggregate to protect.</para>
/// </summary>
public interface IExportRequestRepository
{
    /// <summary>
    /// Inserts a new <c>Pending</c> export request for <paramref name="siteId"/>, owned by
    /// <paramref name="exportId"/> (minted by the caller via <c>IIdGenerator</c>, the same "handler
    /// generates the id, repository just persists it" shape every other write in this codebase uses).
    /// Returns <see langword="false"/> if no site with this id exists - the caller's
    /// <c>Site.NotFound</c> case - and <see langword="true"/> otherwise. Unlike
    /// <see cref="IErasureRequestRepository"/>'s stamp-in-place, this always creates a new row: a
    /// tenant may export more than once, and each attempt gets its own id and its own timeline rather
    /// than collapsing into one idempotent flag.
    /// </summary>
    Task<bool> CreateAsync(
        Guid exportId, SiteId siteId, OperatorId requestedBy, DateTimeOffset requestedAt, CancellationToken cancellationToken);

    /// <summary>
    /// Reads one export request, scoped by both <paramref name="exportId"/> and
    /// <paramref name="siteId"/> - the same "wrong site is indistinguishable from no such id"
    /// cross-tenant guard <see cref="IErasureRequestRepository.RequestConversationErasureAsync"/>'s own
    /// remarks describe, applied to a read instead of a write: an operator polling with the wrong
    /// site's id in the route gets the same <see langword="null"/> as one polling a export id that
    /// never existed, never a cross-tenant existence leak.
    /// </summary>
    Task<ExportRequestRecord?> GetAsync(Guid exportId, SiteId siteId, CancellationToken cancellationToken);
}

/// <summary>Read model for one export request - not a domain aggregate (see
/// <see cref="IExportRequestRepository"/>'s own remarks on why none exists), just the columns a status
/// poll needs.</summary>
public sealed record ExportRequestRecord(
    Guid Id,
    ExportStatus Status,
    string? ObjectKey,
    string? FailureReason,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt);
