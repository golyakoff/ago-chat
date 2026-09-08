using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `23-68`: `operator_seat_restore_overrides` - one row per exercised override, written when the
/// platform owner restores an operator's seat past the site's own current seat limit. Never written for
/// a restore that stayed within the limit - <see cref="OperatorSeatRestoreOverrideEntity"/>'s own
/// remarks and `RestoreOperatorSeatAsOwnerHandler`'s own remarks state why.
///
/// <para><b>No FK to <c>sites</c> or <c>operators</c>, deliberately - the same reason
/// <c>ModuleRevokeOverrideEntityConfiguration</c>'s own <c>SiteId</c> carries none, and
/// <c>AccessRecordEntity</c>'s before that.</b> A tenant whose seat-limit override is later closed (or
/// erased) is exactly the tenant most likely to ask, later, "who let this operator back in, and why" -
/// a cascading foreign key would let the answer disappear with the account, which is the one outcome
/// this record exists to prevent.</para>
/// </summary>
internal sealed class OperatorSeatRestoreOverrideEntityConfiguration : IEntityTypeConfiguration<OperatorSeatRestoreOverrideEntity>
{
    public void Configure(EntityTypeBuilder<OperatorSeatRestoreOverrideEntity> builder)
    {
        builder.ToTable("operator_seat_restore_overrides");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
        // No HasOne<Site>()/HasOne<Operator>()/HasForeignKey - see this type's and
        // OperatorSeatRestoreOverrideEntity's own remarks for why the absence is deliberate, not a gap.
        builder.Property(e => e.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site).IsRequired();
        builder.Property(e => e.OperatorId).HasColumnName("operator_id").HasConversion(IdConverters.Operator).IsRequired();
        builder.Property(e => e.RestoredBy).HasColumnName("restored_by").IsRequired();
        builder.Property(e => e.Reason).HasColumnName("reason").IsRequired();
        builder.Property(e => e.RestoredAt).HasColumnName("restored_at").IsRequired();

        // Not consulted by anything this item builds (no console screen reads it back), but every
        // table in this codebase carries an index for its own site-scoped read (db-migration skill:
        // "multi-tenancy is not optional... every query filters by it") - the one
        // IOperatorSeatRestoreOverrideRepository.ListForSiteAsync needs, and the one a future support
        // screen would need without a migration of its own.
        builder.HasIndex(e => e.SiteId).HasDatabaseName("ix_operator_seat_restore_overrides_site_id");
    }
}
