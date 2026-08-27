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
