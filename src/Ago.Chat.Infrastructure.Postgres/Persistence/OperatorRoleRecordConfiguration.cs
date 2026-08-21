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

        builder.HasOne<Operator>().WithMany().HasForeignKey(x => x.OperatorId);
        builder.HasOne<RoleRecord>().WithMany().HasForeignKey(x => x.RoleId);
    }
}
