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

        // `25-56`: nullable at the storage level too, deliberately not `.IsRequired()` - a real NOT
        // NULL constraint would have to hold for every row any test in this codebase ever inserts, and
        // ~90 existing call sites across this repository's test suite construct a Visitor for a reason
        // that has nothing to do with its emoji pair (Visitor's own remarks make the identical call for
        // the CLR property). The guarantee this system actually makes - every visitor a real caller can
        // ever see has a pair - is enforced where it belongs: Stage25AddVisitorEmojiPair's own backfill
        // for every row that predates this item, and StartConversationHandler/ReceiveChannelMessageHandler
        // calling AssignEmojiPair before the first save for every row created since. A database
        // constraint here would duplicate that guarantee for production data while breaking it for
        // every test fixture that has no reason to know this column exists - the same trade-off this
        // project already makes for other "always true in practice, not DB-enforced" invariants.
        builder.Property(v => v.EmojiCreature).HasColumnName("emoji_creature");
        builder.Property(v => v.EmojiFood).HasColumnName("emoji_food");

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
