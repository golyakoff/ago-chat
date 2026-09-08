using Ago.Chat.Domain;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `23-59`/`adr/0147`: "a grant that succeeds and a carry-over that fails must be two facts, so the
/// second can be re-run without redoing the first" - this row is the second fact.
/// <c>EnableModuleForSiteAsOwnerHandler</c> writes it (through
/// <c>Ago.Chat.Application.Abstractions.IContactCarryoverRequestStore</c>) the instant a grant succeeds; nothing
/// about the potentially-unbounded work of actually walking a site's contact history happens in that
/// request - <c>ContactCarryoverJob</c> (`Ago.Chat.Worker`) does that later, in bounded batches, and
/// this row is what lets it resume rather than restart after any interruption.
///
/// <para><b>One row per site, not one per request.</b> <see cref="SiteId"/> is the primary key: a
/// re-grant of an already-granted module (`22-05`/`23-102`'s own repair path, reused here) resets this
/// row rather than appending a second one, the identical "deliberately unconditional... how a site
/// whose projection went stale before this item existed gets fixed by an ordinary re-grant"
/// reasoning <c>RoleRepository.AddPermissionsAsync</c>'s own remarks give.</para>
///
/// <para><b><see cref="CursorContactId"/> is the resumability itself, not a diagnostic.</b> Null means
/// "never started"; a value means "everything up to and including this id has already been staged to
/// the outbox" - <c>ContactCarryoverJob</c>'s own batches page strictly after it
/// (<c>visitor_contact_details.id &gt; cursor_contact_id</c>), so a process that dies mid-run leaves
/// this exactly where it stopped and the next tick continues from there, never from the start. The
/// same "how far is exactly what is left in the table" resumability
/// <c>ConversationErasureJobOptions.MaxMessageBatchesPerConversation</c>'s own remarks describe for
/// `erasure_requested_at`, applied here to a cursor rather than a boolean flag because one contact
/// carry-over can span many batches where one conversation's erasure spans many statements within a
/// single flagged row.</para>
///
/// <para><b>No status column, unlike <see cref="ErasureRecordEntity"/>.</b> There is no failure state
/// to record: every batch is naturally idempotent (redelivering the identical
/// <see cref="Ago.Chat.Contracts.ContactCollected"/> event twice is a no-op at the far side), so a
/// batch that throws simply leaves this row exactly as it was before the attempt - the next sweep
/// retries it - which is the whole answer to "a failed carry-over can be re-run without re-granting
/// anything" (`23-59`'s own Done-when): nothing has to notice the failure or trigger a re-run, because
/// there was never a status to become <c>Failed</c> in the first place.</para>
/// </summary>
internal sealed class ContactCarryoverRequestEntity
{
    public SiteId SiteId { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    public Guid? CursorContactId { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}
