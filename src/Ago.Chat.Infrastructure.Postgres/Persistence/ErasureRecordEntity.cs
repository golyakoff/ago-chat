using Ago.Chat.Domain;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `24-13`: exists solely so `dotnet ef migrations add` can generate `erasure_records`' own
/// `CREATE TABLE` - the same "migration-scaffolding only, nothing ever queries this DbSet" shape
/// <see cref="ExportRequestEntity"/>'s own remarks give in full, applied to a table that (unlike that
/// one) is never read back by this codebase at all today: there is no status-poll endpoint over
/// `erasure_records` the way `GetSiteExportStatusHandler` polls `export_requests` (Scope named none,
/// and Element 6 of `processing-instruction-facts.md` only asks that the evidence exist, not that an
/// API serve it) - `ErasureRecordQuery` (`Ago.Chat.Worker`) is a write-only counterpart to
/// <see cref="ExportRequestRepository"/> for exactly that reason.
///
/// <para><b>No foreign key to <c>sites</c>, deliberately.</b> Not an oversight the way a missing FK
/// usually would be - the opposite of <see cref="ExportRequestEntityConfiguration"/>'s own
/// `OnDelete(DeleteBehavior.Cascade)` to `Site`. A site-scoped erasure's own record must survive the
/// very `DeleteSiteAsync` its own site erasure performs, or "the record proves an erasure happened"
/// stops being true for the one case a reader would ask about first. This is `adr/0111`'s own
/// mechanism (`acceptance_records.subject_id` carries no FK to `sites`/`operators`/`visitors`, for the
/// same "evidence should not disappear with its own subject's erasure" reason), reused here for a
/// column that names the *tenant*, not a person - <see cref="ErasureRecordEntityConfiguration"/>'s own
/// remarks are explicit about why <see cref="SiteId"/> is not itself the kind of identifier this table
/// exists to keep off its rows.</para>
///
/// <para>Typed <see cref="Domain.SiteId"/>/<see cref="Domain.OperatorId"/> via
/// <see cref="IdConverters"/>, the same as every other entity - a deliberately absent foreign key is
/// still a real column with a real domain type, not a reason to fall back to a bare <see cref="Guid"/>
/// the way genuinely external identifiers (a Keycloak subject id, an S3 object key) do.</para>
/// </summary>
internal sealed class ErasureRecordEntity
{
    public Guid Id { get; set; }

    public string Scope { get; set; } = string.Empty;

    public SiteId SiteId { get; set; }

    public OperatorId RequestedBy { get; set; }

    public string Status { get; set; } = string.Empty;

    public string? FailureReason { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public int MessagesDeleted { get; set; }

    public int AttachmentsDeleted { get; set; }

    public int StorageObjectsDeleted { get; set; }

    public int NotesDeleted { get; set; }

    public int TagsDeleted { get; set; }

    public int ContactDetailsDeleted { get; set; }

    public int ConversationsMarkedForErasure { get; set; }

    public int IdentitiesDeleted { get; set; }
}
