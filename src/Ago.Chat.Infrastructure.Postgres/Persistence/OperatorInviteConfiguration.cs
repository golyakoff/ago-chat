using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

internal sealed class OperatorInviteConfiguration : IEntityTypeConfiguration<OperatorInvite>
{
    public void Configure(EntityTypeBuilder<OperatorInvite> builder)
    {
        builder.ToTable("operator_invites");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).HasColumnName("id").HasConversion(IdConverters.OperatorInvite).ValueGeneratedNever();
        builder.Property(i => i.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site);
        // A plain Guid, not a Domain id type - RoleRecord.Id/OperatorRoleRecord.RoleId are both bare
        // Guids too (RoleRecord's own remarks: roles have no Domain model yet), so this FK matches the
        // type the table it actually points at already uses.
        builder.Property(i => i.RoleId).HasColumnName("role_id");
        builder.Property(i => i.CodeHash).HasColumnName("code_hash").IsRequired();
        builder.Property(i => i.CreatedByOperatorId).HasColumnName("created_by_operator_id").HasConversion(IdConverters.Operator);
        builder.Property(i => i.CreatedAt).HasColumnName("created_at");
        builder.Property(i => i.ExpiresAt).HasColumnName("expires_at");
        builder.Property(i => i.RedeemedAt).HasColumnName("redeemed_at");
        builder.Property(i => i.RedeemedByOperatorId).HasColumnName("redeemed_by_operator_id").HasConversion(IdConverters.NullableOperator);

        builder.HasOne<Site>().WithMany().HasForeignKey(i => i.SiteId);
        builder.HasOne<RoleRecord>().WithMany().HasForeignKey(i => i.RoleId);

        // `code_hash` is how every redemption looks an invite up (OperatorInviteRedemptionRepository) -
        // unique because a hash collision between two genuinely different 256-bit CSPRNG-generated
        // codes should never happen, and a unique index turns "should never happen" into "the database
        // refuses it" the same way OperatorConfiguration's own composite index backstops
        // ISiteRegistrationRepository's compare-and-set.
        builder.HasIndex(i => i.CodeHash).IsUnique().HasDatabaseName("ux_operator_invites_code_hash");

        // `data-model.md`'s `conversations`/`messages` precedent for "Postgres's built-in xmin system
        // column, not an extra column of our own to keep in sync by hand" - the same optimistic-
        // concurrency backstop `ConversationConfiguration` already uses, needed here because two
        // concurrent redemption attempts against the *same* code can both pass
        // `OperatorInviteRedemptionRepository`'s own pre-transaction "not already redeemed" read before
        // either writes anything; `xmin` is what stops the second `SaveChangesAsync` from silently
        // overwriting the first redemption's already-committed row instead of throwing.
        builder.Property<uint>("xmin").IsRowVersion();
    }
}
