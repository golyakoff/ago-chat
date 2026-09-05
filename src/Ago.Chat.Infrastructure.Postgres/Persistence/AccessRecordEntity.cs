using Ago.Chat.Domain;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `24-12`: exists solely so `dotnet ef migrations add` can generate `access_records`' own
/// `CREATE TABLE` - the same "migration-scaffolding only, nothing ever queries this DbSet" shape
/// <see cref="ExportRequestEntity"/>/<see cref="ErasureRecordEntity"/>'s own remarks give in full.
/// <see cref="AccessRecordRepository"/> is raw Npgsql end to end, for the identical reason those two
/// give: a receipt with no aggregate behind it has nothing an EF change-tracked load-mutate-save buys
/// it, and this table is never mutated after insert (unlike <c>erasure_records</c>, which a background
/// job updates in place - an access record is written once, by the handler or endpoint that observed
/// the access, and never touched again).
///
/// <para><b>No FK to <c>sites</c>, deliberately - the same reason <see cref="ErasureRecordEntity"/>'s
/// own <see cref="SiteId"/> carries none.</b> A record of who read this tenant's data must survive
/// <c>SiteErasureJob</c>'s own <c>DELETE FROM sites</c>, or the one tenant whose access history a
/// departing customer most wants an honest answer for is exactly the one a cascade would erase first.
/// <see cref="SiteId"/> is nullable for the one access kind that spans every tenant at once
/// (<c>AccessRecordKind.OwnerSiteList</c>) - see <see cref="Ago.Chat.Application.Abstractions.AccessRecordToWrite"/>'s
/// own remarks.</para>
///
/// <para><b>No FK from <see cref="ResourceId"/> either, for the identical reason.</b> A conversation
/// named here as the one an operator opened must remain nameable in this row after that very
/// conversation is later erased - the same survive-the-subject's-own-erasure mechanism <c>adr/0111</c>/
/// <c>adr/0112</c> already established, reused a third time.</para>
/// </summary>
internal sealed class AccessRecordEntity
{
    public Guid Id { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public string AccessKind { get; set; } = string.Empty;

    public SiteId? SiteId { get; set; }

    public string ActorKind { get; set; } = string.Empty;

    public string ActorId { get; set; } = string.Empty;

    public string? ResourceKind { get; set; }

    public Guid? ResourceId { get; set; }
}
