using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

internal sealed class PendingPhoneVerificationConfiguration : IEntityTypeConfiguration<PendingPhoneVerification>
{
    public void Configure(EntityTypeBuilder<PendingPhoneVerification> builder)
    {
        builder.ToTable("pending_phone_verifications");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id)
            .HasColumnName("id").HasConversion(IdConverters.PendingPhoneVerification).ValueGeneratedNever();
        builder.Property(p => p.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site);
        builder.Property(p => p.VisitorId).HasColumnName("visitor_id").HasConversion(IdConverters.Visitor);

        // Ago.Calendar.Domain.PhoneNumber's own precedent for storing the canonical E.164 string as a
        // plain column - PhoneNumber's own remarks on why it is a Domain value type here, not a fact left
        // to Infrastructure to normalise.
        builder.Property(p => p.Phone).HasColumnName("phone").HasMaxLength(20).IsRequired();

        builder.Property(p => p.CodeHash).HasColumnName("code_hash").IsRequired();

        // Stored as the CLR member name - ChannelIdentityConfiguration's own precedent for every enum in
        // this schema.
        builder.Property(p => p.DeliveryMethod).HasColumnName("delivery_method").HasConversion<string>().HasMaxLength(16);

        builder.Property(p => p.CreatedAt).HasColumnName("created_at");
        builder.Property(p => p.ExpiresAt).HasColumnName("expires_at");
        builder.Property(p => p.ConsumedAt).HasColumnName("consumed_at");
        builder.Property(p => p.AttemptCount).HasColumnName("attempt_count");
        builder.Property(p => p.MaxAttempts).HasColumnName("max_attempts");

        builder.HasOne<Site>().WithMany().HasForeignKey(p => p.SiteId);
        builder.HasOne<Visitor>().WithMany().HasForeignKey(p => p.VisitorId);

        // ConfirmPhoneVerificationHandler's own lookup is by primary key alone (IPendingPhoneVerificationRepository.GetByIdAsync's
        // own remarks on why this port needs no FindLive-shaped query) - no extra index beyond the
        // primary key and the FK indexes EF already creates for the two HasOne calls above.
    }
}
