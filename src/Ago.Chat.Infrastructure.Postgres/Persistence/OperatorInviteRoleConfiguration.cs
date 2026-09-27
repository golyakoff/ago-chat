using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>`26-241`: the pending-side join between an operator invite and the `roles` it grants - the
/// mirror of <see cref="OperatorRoleRecordConfiguration"/>'s own `operator_roles`, for the invite that
/// has not been redeemed yet. Composite `(operator_invite_id, role_id)` primary key, so the same role
/// can never appear twice on one invite, the same shape `operator_roles` uses for the redeemed side.</summary>
internal sealed class OperatorInviteRoleConfiguration : IEntityTypeConfiguration<OperatorInviteRole>
{
    public void Configure(EntityTypeBuilder<OperatorInviteRole> builder)
    {
        builder.ToTable("operator_invite_roles");
        builder.HasKey(x => new { x.OperatorInviteId, x.RoleId });
        builder.Property(x => x.OperatorInviteId).HasColumnName("operator_invite_id").HasConversion(IdConverters.OperatorInvite);
        // A plain Guid, not a Domain id type - RoleRecord.Id/OperatorRoleRecord.RoleId are both bare
        // Guids too (RoleRecord's own remarks: roles have no Domain model yet), so this FK matches the
        // type the table it actually points at already uses.
        builder.Property(x => x.RoleId).HasColumnName("role_id");

        // FK to the role catalogue - the identical HasOne<RoleRecord> the redeemed side
        // (OperatorRoleRecordConfiguration) already draws, so a pending invite can never name a role that
        // does not exist on the site. The parent FK back to operator_invites (with its cascade delete) is
        // configured from the OperatorInvite side (OperatorInviteConfiguration.HasMany), not here.
        builder.HasOne<RoleRecord>().WithMany().HasForeignKey(x => x.RoleId);
    }
}
