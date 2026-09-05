using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `24-11`: builds one subject-scoped export archive - the narrower sibling of what `16-03`'s
/// `SiteExportArchiveWriter` builds for a whole tenant. Its own port, in `Application/Abstractions`
/// rather than a class Application calls directly - the same dependency-rule reasoning every other
/// port in this file gives: <c>ExportConversationHandler</c>/<c>ExportVisitorHandler</c> need to
/// query Postgres (raw SQL, no aggregate to load-mutate-save through - the same shape
/// <see cref="IExportRequestRepository"/>'s own remarks give), and Application must not reference
/// Npgsql directly (`clean-architecture.md`'s dependency rule). The alternative - putting this SQL
/// straight in the handler - would make the handler untestable without a real database and would put
/// infrastructure code above the line the dependency rule draws.
///
/// <para><b>Why this is not <c>SiteExportArchiveWriter</c> widened with a filter.</b> That class lives
/// in <c>Ago.Chat.Worker</c> (a host project) - a deliberate, pre-existing departure this item does not
/// revisit, where a background job does its own raw Npgsql directly rather than through an
/// Application-layer port (<c>SiteErasureQuery</c>/<c>ConversationErasureQuery</c> already establish
/// the same shape for erasure). That works for <c>SiteExportJob</c> because nothing outside
/// <c>Ago.Chat.Worker</c> ever calls it. This port's callers are <c>Ago.Chat.Api</c> request handlers,
/// which - unlike a Worker job - must reach Postgres through a port the dependency rule allows them to
/// hold (rule 2: every external resource sits behind a port declared in Application and implemented in
/// Infrastructure). Moving <c>SiteExportArchiveWriter</c> itself into <c>Infrastructure.Postgres</c> to
/// share code was considered and rejected for this change: it is working, tested code with no defect
/// this item found, and the risk of a refactor that touches it is not worth paying to avoid a second,
/// smaller writer class. The two writers agree on the wire format (one `.zip`, `manifest.json` plus one
/// JSON Lines file per store, the same `formatVersion`) - see this port's own implementation for the
/// per-store shapes it reuses.</para>
///
/// <para><b>Why this runs synchronously in a request handler rather than through `16-03`'s
/// Pending/Ready/Failed job queue.</b> Two independent reasons, both real: first, the data volume this
/// scope can ever produce is bounded by "one conversation" or "one visitor's own history", never "a
/// tenant's full history" - the reason `16-03` needed an asynchronous job at all (`SiteExportJobOptions`'s
/// own remarks: "a tenant with a year of conversations must not require the API to hold it all in
/// memory") does not transfer to a scope this small. Second, this session is under a schema-migration
/// freeze - `23-06`'s own migration holds the slot - and `export_requests` has no column that says
/// which conversation or visitor a request is scoped to; adding one is exactly the kind of change the
/// freeze exists to defer. Both reasons are stated because either alone would have been enough, and a
/// reader should not have to guess which one this item actually depended on.</para>
/// </summary>
public interface IPersonExportArchiveWriter
{
    /// <summary>
    /// Builds one subject-scoped archive onto a local temp file (the same "stream row-by-row through a
    /// forward-only reader into a `ZipArchive` entry, never buffer a list" discipline
    /// <c>SiteExportArchiveWriter</c> already establishes) and returns it as an open, readable
    /// <see cref="Stream"/> positioned at the start, opened with <see cref="FileOptions.DeleteOnClose"/>
    /// so the temp file is removed the moment the caller disposes the stream - no separate cleanup step
    /// for the caller to forget, unlike <c>SiteExportJob.ProcessExportAsync</c>'s own `finally`-block
    /// delete (that one cannot use `DeleteOnClose` because it re-opens the same file for the upload
    /// step; this port hands the same file straight to its one reader instead).
    ///
    /// <para><paramref name="conversationIds"/> is always at least one id - a conversation-scoped
    /// export passes exactly the one conversation the operator is exporting;
    /// a visitor-scoped export passes every conversation <see cref="IConversationReadStore.ListAllForVisitorAsync"/>
    /// found for that visitor, the anchor conversation included. <paramref name="scope"/> is manifest
    /// metadata only ("conversation" or "visitor") - it changes nothing about which rows this method
    /// reads, only what the archive's own `manifest.json` says about why it was produced, so a person
    /// who receives the file can tell which request produced it.</para>
    /// </summary>
    Task<Stream> WriteAsync(
        SiteId siteId,
        VisitorId visitorId,
        IReadOnlyList<ConversationId> conversationIds,
        string scope,
        DateTimeOffset exportedAt,
        CancellationToken cancellationToken);
}

/// <summary>The finished archive, handed back up through the handler to the endpoint that streams it
/// as the HTTP response body (`Results.File`, which both writes and disposes <see cref="Content"/> -
/// see <see cref="IPersonExportArchiveWriter.WriteAsync"/>'s own remarks on why disposal alone is
/// enough to clean up the temp file behind it).</summary>
public sealed record PersonExportArchive(Stream Content, string FileName);
