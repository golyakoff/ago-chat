using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

internal sealed class RoleChangeRecordConfiguration : IEntityTypeConfiguration<RoleChangeRecordEntity>
{
    public void Configure(EntityTypeBuilder<RoleChangeRecordEntity> builder)
    {
        builder.ToTable("role_change_records");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(e => e.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site).IsRequired();
        // `25-41`: nullable - IdConverters.NullableOperator, not the non-nullable IdConverters.Operator
        // every other column here still uses. RoleChangeRecordEntity's own remarks state why: an
        // automatic demotion (AdministratorLimitEnforcer) has no operator to name here honestly.
        builder.Property(e => e.ChangedByOperatorId).HasColumnName("changed_by_operator_id").HasConversion(IdConverters.NullableOperator);
        builder.Property(e => e.ChangedOperatorId).HasColumnName("changed_operator_id").HasConversion(IdConverters.Operator).IsRequired();
        // Postgres text[], the identical native List<string> mapping RoleRecord.Permissions already
        // uses (RoleRecordConfiguration's own remarks) - the operator's whole previous role set, which
        // can be more than one name (RoleChangeRecordToWrite's own remarks).
        builder.Property(e => e.PreviousRoleNames).HasColumnName("previous_role_names").IsRequired();
        builder.Property(e => e.NewRoleName).HasColumnName("new_role_name").IsRequired();
        builder.Property(e => e.ChangedAt).HasColumnName("changed_at").IsRequired();

        // Ordinary tenant data - real foreign keys, cascading with the site and the two operators named,
        // the identical default EF already gives operator_roles for a required relationship with no
        // explicit OnDelete call (OperatorRoleRecordConfiguration's own remarks) - unlike access_records,
        // this table has no reason to survive the tenant's own erasure (RoleChangeRecordEntity's own
        // remarks). Left as EF's own convention default (Cascade) rather than named explicitly, matching
        // that precedent, and deliberately not Restrict on either operator FK - a site's own erasure
        // cascades to its operators, and this table's own site_id FK already cascades those rows away in
        // the same statement, so a Restrict here would only ever turn a legitimate cascade into a
        // foreign-key violation.
        builder.HasOne<Site>().WithMany().HasForeignKey(e => e.SiteId);
        builder.HasOne<Operator>().WithMany().HasForeignKey(e => e.ChangedByOperatorId);
        builder.HasOne<Operator>().WithMany().HasForeignKey(e => e.ChangedOperatorId);

        // The one read this table might ever serve if a future item builds one - a tenant's own history,
        // newest first, the same "index the columns a keyset read would use together" reasoning
        // AccessRecordEntityConfiguration's own ix_access_records_site_id_id already states.
        builder.HasIndex(e => new { e.SiteId, e.ChangedAt }).HasDatabaseName("ix_role_change_records_site_id_changed_at");
    }
}
