using Ago.Chat.Domain;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `23-11`: exists solely so `dotnet ef migrations add` can generate `contact_reveals`' own
/// `CREATE TABLE` - the same "migration-scaffolding only, nothing ever queries this DbSet" shape
/// <see cref="AccessRecordEntity"/>'s own remarks give in full. <see cref="ContactRevealRepository"/>
/// is raw Npgsql end to end, for the identical reason: a receipt with no aggregate behind it has
/// nothing an EF change-tracked load-mutate-save buys it, and this table is never mutated after
/// insert.
///
/// <para><b>No FK to <c>sites</c> or to <c>visitor_contact_details</c>, deliberately - the same reason
/// <see cref="AccessRecordEntity"/>'s own <c>SiteId</c>/<c>ResourceId</c> carry none.</b> A record of
/// who revealed this visitor's contact must survive both `SiteErasureJob`'s own site deletion and
/// `23-08`'s own conversation-scoped contact-detail erasure, or the one question this table exists to
/// answer ("who revealed this before it was deleted") would have its evidence destroyed by the very
/// process the question is about.</para>
/// </summary>
internal sealed class ContactRevealEntity
{
    public Guid Id { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public SiteId SiteId { get; set; }

    public Guid ConversationId { get; set; }

    public Guid ContactDetailId { get; set; }

    public Guid OperatorId { get; set; }

    public string Surface { get; set; } = string.Empty;
}
