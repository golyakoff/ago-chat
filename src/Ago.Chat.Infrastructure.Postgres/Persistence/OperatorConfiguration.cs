using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

internal sealed class OperatorConfiguration : IEntityTypeConfiguration<Operator>
{
    public void Configure(EntityTypeBuilder<Operator> builder)
    {
        builder.ToTable("operators");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).HasColumnName("id").HasConversion(IdConverters.Operator).ValueGeneratedNever();
        builder.Property(o => o.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site);
        builder.Property(o => o.Status).HasColumnName("status").HasConversion<string>();
        builder.Property(o => o.Capacity).HasColumnName("capacity");
        // `5-05`/`adr/0022`: nullable - not every existing row has one, and there is no backfill for
        // a Keycloak identity that never existed.
        //
        // `13-07`/`adr/0068`: the index widens from single-column globally-unique to composite
        // `(external_subject_id, site_id)`, still unique when present - "at most one `Operator` row
        // per identity per `Site`", which was already true of every row that has ever existed (the
        // old, stricter key made it true trivially), rather than "at most one `Operator` row per
        // identity anywhere". This is the one schema change the whole "one login, several tenants"
        // mechanism rests on: `ResolveOperatorIdentityHandler` now expects more than one row per
        // `external_subject_id` to be a normal, indexable shape, not an anomaly. Migration
        // `Stage13RelaxOperatorIdentityUniqueness`.
        builder.Property(o => o.ExternalSubjectId).HasColumnName("external_subject_id");
        builder.HasIndex(o => new { o.ExternalSubjectId, o.SiteId })
            .IsUnique()
            .HasFilter("external_subject_id IS NOT NULL");

        // `23-02`: nullable, no default, no index - neither column is ever queried by value (nothing
        // looks an operator up by name or email; identity is still `ExternalSubjectId` alone), so this
        // is exactly the "no invariant, one column" shape `HoldsSeat`/`RemovedAt` above already use,
        // not a second lookup key. Written only by `OperatorInviteRedemptionRepository`/
        // `RegisterSiteHandler` at creation and by `IOperatorRepository.RefreshIdentityAsync`'s raw SQL
        // at sign-in - never through this aggregate's own SaveChanges path, so this column's presence
        // here is purely "EF must know the table has it," the same reason `active_chats` is a shadow
        // property a few lines down, except these two are real CLR properties with nothing to hide from
        // ordinary reads.
        builder.Property(o => o.DisplayName).HasColumnName("display_name");
        builder.Property(o => o.Email).HasColumnName("email");

        // `13-03`: the seat-assignment and operator-removal columns - see each property's own remarks
        // on Operator for who writes them and why. HoldsSeat defaults true at the database level too,
        // matching the CLR default (`Operator`'s own constructor default) - belt and braces for any
        // future raw-SQL insert that bypasses the aggregate.
        builder.Property(o => o.HoldsSeat).HasColumnName("holds_seat").HasDefaultValue(true);
        builder.Property(o => o.RemovedAt).HasColumnName("removed_at");

        // `13-03`: serves OperatorInviteRedemptionRepository's own fixed regression
        // (`AND removed_at IS NULL`) and GetSeatAssignmentSummaryHandler's own count - both filter on
        // `site_id` plus a live/removed distinction, so this indexes exactly the pair either query
        // actually reads together, the same "index the column a real caller filters by" shape this
        // codebase already uses everywhere else.
        builder.HasIndex(o => new { o.SiteId, o.RemovedAt }).HasDatabaseName("ix_operators_site_id_removed_at");

        // 4-01: a shadow property, not a CLR property on Operator - EF needs to know this column
        // exists so `dotnet ef migrations add` generates a real ALTER TABLE from the model diff, but
        // nothing may ever write it through SaveChanges. The only writer is OperatorCapacityStore's
        // atomic `UPDATE ... WHERE active_chats < capacity` (IOperatorCapacity) - an EF load-mutate-
        // save race against that raw SQL is exactly the failure mode this port exists to avoid, so
        // the aggregate must have no way to touch the column at all, not just a convention not to.
        builder.Property<int>("active_chats").HasColumnName("active_chats").HasDefaultValue(0);

        builder.HasOne<Site>().WithMany().HasForeignKey(o => o.SiteId);
    }
}
