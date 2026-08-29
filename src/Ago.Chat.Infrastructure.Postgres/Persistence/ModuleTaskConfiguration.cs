using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>`20-07`: one <see cref="ModuleTask"/> - reached only through
/// <see cref="Conversation.ActiveModuleTask"/>/the private <c>_moduleTasks</c> navigation
/// (<see cref="ConversationConfiguration"/>), never through its own <see cref="DbSet{TEntity}"/> -
/// the same "no public setters, EF materializes through the aggregate's own field" shape
/// <see cref="MessageConfiguration"/> already establishes for <see cref="Message"/>.</summary>
internal sealed class ModuleTaskConfiguration : IEntityTypeConfiguration<ModuleTask>
{
    public void Configure(EntityTypeBuilder<ModuleTask> builder)
    {
        builder.ToTable("module_tasks");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id").HasConversion(IdConverters.ModuleTask).ValueGeneratedNever();
        builder.Property(t => t.ConversationId).HasColumnName("conversation_id").HasConversion(IdConverters.Conversation);
        builder.Property(t => t.ModuleKey).HasColumnName("module_key")
            .HasMaxLength(ModuleKey.MaxLength).HasConversion(IdConverters.ModuleKey);
        builder.Property(t => t.ExternalTaskId).HasColumnName("external_task_id").HasMaxLength(512);
        builder.Property(t => t.State).HasColumnName("state").HasConversion<string>();
        builder.Property(t => t.OpenedAt).HasColumnName("opened_at");
        builder.Property(t => t.ClosedAt).HasColumnName("closed_at");

        // The same three-column shape MessageConfiguration uses for a message's own structured
        // content, and the same converters - a module task's "last recorded step" is exactly a
        // MessageContentKind/MessagePayload/list-of-MessageAction triple, so there is no reason to
        // invent a second set of converters for an identical value shape.
        builder.Property(t => t.LastStepKind).HasColumnName("last_step_kind")
            .HasMaxLength(MessageContentKind.MaxLength).HasConversion(MessageContentConverters.Kind);
        builder.Property(t => t.LastStepPayload).HasColumnName("last_step_payload")
            .HasConversion(MessageContentConverters.Payload);
        // `_lastStepActions` is `List<MessageAction>?`, matching `Message._actions`' own shape exactly,
        // so `MessageContentConverters.Actions` applies unchanged - the same converter, no second one
        // to keep in sync with the first.
        builder.Property<List<MessageAction>?>("_lastStepActions").HasColumnName("last_step_actions")
            .HasConversion(MessageContentConverters.Actions, MessageContentConverters.ActionsComparer);
        builder.Ignore(t => t.LastStepActions);

        // The relationship itself (foreign key, cascade delete, field-only navigation) is configured
        // entirely from Conversation's own side (ConversationConfiguration), matching how
        // Conversation/Message's identical shadow-collection relationship is configured there and not
        // here - one relationship, configured once, is what keeps the two files from silently
        // disagreeing about it.

        // `20-07`/`adr/0065` decision 7's "at most one active task per conversation" as an actual
        // storage-level backstop, not only the aggregate's own in-memory check
        // (Conversation.StartModuleTask) - the identical "the index is the backstop, not the primary
        // mechanism" split adr/0019 draws for messages, applied here to a second writer racing the
        // same conversation's own StartModuleTask concurrently.
        builder.HasIndex(t => t.ConversationId)
            .HasDatabaseName("ux_module_tasks_conversation_active")
            .IsUnique()
            .HasFilter("state = 'Open'");
    }
}
