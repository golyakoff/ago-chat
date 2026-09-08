using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `23-59`: `contact_carryover_requests` - see <see cref="ContactCarryoverRequestEntity"/>'s own
/// remarks for what each column means.
/// </summary>
internal sealed class ContactCarryoverRequestEntityConfiguration : IEntityTypeConfiguration<ContactCarryoverRequestEntity>
{
    public void Configure(EntityTypeBuilder<ContactCarryoverRequestEntity> builder)
    {
        builder.ToTable("contact_carryover_requests");
        builder.HasKey(r => r.SiteId);
        builder.Property(r => r.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site).ValueGeneratedNever();
        builder.Property(r => r.RequestedAt).HasColumnName("requested_at").IsRequired();
        builder.Property(r => r.CursorContactId).HasColumnName("cursor_contact_id");
        builder.Property(r => r.CompletedAt).HasColumnName("completed_at");

        // No FK to `sites` - the same deliberate absence `ErasureRecordEntityConfiguration`'s own
        // remarks explain, restated for the identical reason: `SiteId` here names a tenant, not a
        // person, so there is no personal-data argument for the omission the way there is on that
        // table - this one is a plain FK-eligible reference, kept off only because nothing about this
        // row's own lifetime needs to survive `DeleteSiteAsync` (a site being erased has nothing left
        // for a carry-over to find either way, so the two rows disappearing together is correct).
        builder.HasOne<Site>().WithMany().HasForeignKey(r => r.SiteId).OnDelete(DeleteBehavior.Cascade);

        // `ContactCarryoverJob.SweepAsync`'s own read: every site not yet completed, oldest request
        // first - the one real query this table has, so (unlike ErasureRecordEntityConfiguration's own
        // "no index: nothing queries this table" note) an index arrives with it.
        builder.HasIndex(r => new { r.CompletedAt, r.RequestedAt }).HasDatabaseName("ix_contact_carryover_requests_pending");
    }
}
