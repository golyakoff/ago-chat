using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

internal sealed class OperatorRoleRecordConfiguration : IEntityTypeConfiguration<OperatorRoleRecord>
{
    public void Configure(EntityTypeBuilder<OperatorRoleRecord> builder)
    {
        builder.ToTable("operator_roles");
        builder.HasKey(x => new { x.OperatorId, x.RoleId });
        builder.Property(x => x.OperatorId).HasColumnName("operator_id").HasConversion(IdConverters.Operator);
        builder.Property(x => x.RoleId).HasColumnName("role_id");

        // `25-170`: the seat-holding and grant-time facts moved here from `operators.holds_seat` - see
        // OperatorRoleRecord's own remarks. HoldsSeat defaults true at the database level too, matching
        // the CLR default (the same "belt and braces for any future raw-SQL insert" reasoning
        // `OperatorConfiguration`'s own pre-`25-170` remarks gave `operators.holds_seat`'s identical
        // default). GrantedAt has no database default - every writer stamps it explicitly with its own
        // IClock.UtcNow (CLAUDE.md rule 11), the same "no DateTime.Now anywhere but IClock" discipline
        // every other timestamp column in this codebase already follows.
        builder.Property(x => x.HoldsSeat).HasColumnName("holds_seat").HasDefaultValue(true);
        builder.Property(x => x.GrantedAt).HasColumnName("granted_at");

        builder.HasOne<Operator>().WithMany().HasForeignKey(x => x.OperatorId);
        builder.HasOne<RoleRecord>().WithMany().HasForeignKey(x => x.RoleId);
    }
}
