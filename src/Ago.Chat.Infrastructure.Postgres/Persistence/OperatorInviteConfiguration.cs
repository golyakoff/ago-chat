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
        // `25-73`: required since this item - see OperatorInvite.Email's own remarks. The migration
        // that adds this column also deletes every pre-existing row (this item's own point 8, "annulled
        // outright"), so the column carries no default and needed none: the table is empty the moment
        // it is added.
        builder.Property(i => i.Email).HasColumnName("email").IsRequired();
        builder.Property(i => i.CreatedByOperatorId).HasColumnName("created_by_operator_id").HasConversion(IdConverters.Operator);
        builder.Property(i => i.CreatedAt).HasColumnName("created_at");
        builder.Property(i => i.ExpiresAt).HasColumnName("expires_at");
        builder.Property(i => i.RedeemedAt).HasColumnName("redeemed_at");
        builder.Property(i => i.RedeemedByOperatorId).HasColumnName("redeemed_by_operator_id").HasConversion(IdConverters.NullableOperator);
        // `25-73`: RevokeOperatorInviteHandler's own write, SendFailed's own record-keeping - both
        // optional, the same "absence is the ordinary case" shape RedeemedAt/RedeemedByOperatorId
        // already establish just above for this identical aggregate.
        builder.Property(i => i.RevokedAt).HasColumnName("revoked_at");
        builder.Property(i => i.SendFailureCode).HasColumnName("send_failure_code");

        builder.HasOne<Site>().WithMany().HasForeignKey(i => i.SiteId);
        builder.HasOne<RoleRecord>().WithMany().HasForeignKey(i => i.RoleId);

        // `code_hash` is how every redemption looks an invite up (OperatorInviteRedemptionRepository) -
        // unique because a hash collision between two genuinely different 256-bit CSPRNG-generated
        // codes should never happen, and a unique index turns "should never happen" into "the database
        // refuses it" the same way OperatorConfiguration's own composite index backstops
        // ISiteRegistrationRepository's compare-and-set.
        builder.HasIndex(i => i.CodeHash).IsUnique().HasDatabaseName("ux_operator_invites_code_hash");

        // `25-73`: named explicitly, in this project's own lower-snake-case convention
        // (`ux_operator_invites_code_hash` right above) - EF Core already creates an index for this FK
        // by convention (`IX_operator_invites_site_id`), which the generated migration renames rather
        // than adds; stated here so the index OperatorInviteListReadStore's own new per-site query
        // relies on is named deliberately, not left as a default this file never mentions.
        builder.HasIndex(i => i.SiteId).HasDatabaseName("ix_operator_invites_site_id");

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
