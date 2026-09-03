using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>`20-07`: "site X has module K enabled" - the registry row (`adr/0065` decision 2).</summary>
internal sealed class EnabledModuleConfiguration : IEntityTypeConfiguration<EnabledModule>
{
    public void Configure(EntityTypeBuilder<EnabledModule> builder)
    {
        builder.ToTable("enabled_modules");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).HasColumnName("id").HasConversion(IdConverters.EnabledModule).ValueGeneratedNever();
        builder.Property(m => m.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site);
        builder.Property(m => m.ModuleKey).HasColumnName("module_key")
            .HasMaxLength(ModuleKey.MaxLength).HasConversion(IdConverters.ModuleKey);

        // A JSON array in one text column, the same "small, bounded, never queried into" shape
        // MessageContentConverters.Actions uses for a message's own actions list - trigger words are
        // read as a whole list (TriggerCommandMatcher) or not at all, never filtered by Postgres.
        builder.Property(m => m.TriggerWords).HasColumnName("trigger_words")
            .HasConversion(TriggerWordsConverter.Instance, TriggerWordsConverter.Comparer);

        builder.Property(m => m.EntryPoint).HasColumnName("entry_point")
            .HasConversion(u => u.ToString(), value => new Uri(value, UriKind.Absolute));

        // `22-02`: the credential this site's calls prove themselves with - see ModuleCredential's own
        // remarks. No HasMaxLength beyond the value object's own MaxLength: the same "the value object
        // is the single source of truth for shape" rule ModuleKey's own conversion already follows.
        builder.Property(m => m.Credential).HasColumnName("credential")
            .HasMaxLength(ModuleCredential.MaxLength)
            .HasConversion(c => c.Value, value => new ModuleCredential(value));

        builder.Property(m => m.EnabledAt).HasColumnName("enabled_at");

        builder.HasOne<Site>().WithMany().HasForeignKey(m => m.SiteId);

        // `20-07`'s own trigger-conflict rule (EnableModuleForSiteHandler) reads every enabled module
        // for a site to check for an overlapping trigger word - the same "the hot read for this
        // aggregate" reasoning ix_conversations_waiting gives for its own index.
        builder.HasIndex(m => m.SiteId).HasDatabaseName("ix_enabled_modules_site");
    }
}
