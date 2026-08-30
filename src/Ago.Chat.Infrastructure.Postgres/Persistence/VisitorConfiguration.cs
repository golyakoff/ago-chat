using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

internal sealed class VisitorConfiguration : IEntityTypeConfiguration<Visitor>
{
    public void Configure(EntityTypeBuilder<Visitor> builder)
    {
        builder.ToTable("visitors");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).HasColumnName("id").HasConversion(IdConverters.Visitor).ValueGeneratedNever();
        builder.Property(v => v.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site);
        builder.Property(v => v.FirstSeenAt).HasColumnName("first_seen_at");
        builder.Property(v => v.LastSeenAt).HasColumnName("last_seen_at");

        // `14-13`/`adr/0079` decision 5 - nullable, the same "no value for every row" shape
        // Conversation.OperatorId already establishes for this table's own nullable FK.
        builder.Property(v => v.PreferredChannelIdentityId)
            .HasColumnName("preferred_channel_identity_id")
            .HasConversion(IdConverters.NullableChannelIdentity);

        // Not a navigation on Visitor (aggregates stay independent - data-model.md lists site_id as
        // a plain foreign key, never a loaded Site) - HasOne/WithMany with no exposed property is how
        // EF adds the DB-level constraint without adding a Domain reference to Site.
        builder.HasOne<Site>().WithMany().HasForeignKey(v => v.SiteId);

        // `14-13`: the identical "constraint at the storage level, no Domain reference" shape right
        // above, applied to the new nullable preference column - ChannelIdentityConfiguration's own
        // primary key is what this points at.
        builder.HasOne<ChannelIdentity>().WithMany().HasForeignKey(v => v.PreferredChannelIdentityId);
    }
}
