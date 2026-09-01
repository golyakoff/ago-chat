using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

internal sealed class ModuleTaskChannelPreferenceConfiguration : IEntityTypeConfiguration<ModuleTaskChannelPreference>
{
    public void Configure(EntityTypeBuilder<ModuleTaskChannelPreference> builder)
    {
        builder.ToTable("module_task_channel_preferences");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id)
            .HasColumnName("id").HasConversion(IdConverters.ModuleTaskChannelPreference).ValueGeneratedNever();
        builder.Property(p => p.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site);
        builder.Property(p => p.ModuleTaskId).HasColumnName("module_task_id").HasConversion(IdConverters.ModuleTask);
        builder.Property(p => p.VisitorId).HasColumnName("visitor_id").HasConversion(IdConverters.Visitor);
        builder.Property(p => p.ChannelIdentityId)
            .HasColumnName("channel_identity_id").HasConversion(IdConverters.ChannelIdentity);
        builder.Property(p => p.Priority).HasColumnName("priority");
        builder.Property(p => p.AddedAt).HasColumnName("added_at");

        // `20-11`: the storage-level backstop for "1-based, unique within one booking" -
        // ChannelIdentityConfiguration's own "the index is the backstop, not the primary mechanism"
        // division. ReplaceForModuleTaskAsync always deletes the old rows before inserting the new ones
        // in the same transaction, so this never actually fires in ordinary operation; it exists to make
        // a future write path that forgets to do that fail loudly instead of silently corrupting order.
        builder.HasIndex(p => new { p.ModuleTaskId, p.Priority })
            .IsUnique()
            .HasDatabaseName("ux_module_task_channel_preferences_module_task_priority");

        // The same channel identity cannot appear twice in one booking's own list -
        // SetModuleTaskChannelPriorityListHandler's own application-level duplicate check, backstopped here.
        builder.HasIndex(p => new { p.ModuleTaskId, p.ChannelIdentityId })
            .IsUnique()
            .HasDatabaseName("ux_module_task_channel_preferences_module_task_channel_identity");

        // The read path's own lookup key (IModuleTaskChannelPreferenceRepository.ListForModuleTaskAsync) -
        // covered by the two composite indexes above, but named explicitly here to document intent
        // (a plain single-column index on module_task_id alone would be redundant with either composite
        // index's own leading column, so none is added separately).

        // Not navigations - the same "aggregates stay independent" shape ChannelIdentityConfiguration's
        // own remarks describe for its own foreign keys, applied here to all four references including
        // ModuleTask - its own table (module_tasks, ModuleTaskConfiguration) is a real EF entity type
        // even though nothing outside Conversation ever loads it through a navigation, so a plain
        // HasOne<ModuleTask>() still adds a genuine FK constraint without adding a Domain reference or a
        // second collection navigation for Conversation to disagree with.
        builder.HasOne<Site>().WithMany().HasForeignKey(p => p.SiteId);
        builder.HasOne<Visitor>().WithMany().HasForeignKey(p => p.VisitorId);
        builder.HasOne<ChannelIdentity>().WithMany().HasForeignKey(p => p.ChannelIdentityId);
        builder.HasOne<ModuleTask>().WithMany().HasForeignKey(p => p.ModuleTaskId);
    }
}
