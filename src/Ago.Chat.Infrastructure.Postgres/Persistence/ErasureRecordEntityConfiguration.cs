using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `24-13`: `erasure_records` - a receipt proving an erasure happened, deliberately holding nothing
/// that could single out the person it was about. Every column below is checked against exactly that
/// question, not against "does this look like personal data" in the abstract:
/// <list type="bullet">
/// <item><see cref="ErasureRecordEntity.SiteId"/> names a tenant (a business account), never a
/// visitor - the same distinction <c>personal-data.md</c>'s own AGO Calendar rows draw between a
/// product's incidental personal data and its deliberate one. Knowing *which shop* ran an erasure
/// does not single out *which visitor* it was for.</item>
/// <item><see cref="ErasureRecordEntity.RequestedBy"/> names the operator who asked, not the person
/// erased - evidence of who acted, the same role `requested_by` already plays on
/// <c>export_requests</c>.</item>
/// <item><b>No <c>conversation_id</c>, no <c>visitor_id</c>, anywhere on this row - the one property
/// this item exists to build safely, not a detail.</b> A conversation-scoped erasure's own receipt
/// says only that *some* conversation on this site was erased, by this operator, at this time, with
/// these counts - never which one. `conversations.erasure_record_id` (the reverse arrow: the
/// about-to-be-deleted conversation points at its own receipt, not the other way around) is what lets
/// <c>ConversationErasureJob</c> find the right row to update without this table ever holding
/// anything that points back.</item>
/// <item><see cref="ErasureRecordEntity.FailureReason"/> is an exception **type name**
/// (<c>ErasureRecordQuery</c>'s own remarks), never an exception message - a message can quote a
/// value (an object key, a connection string fragment); a type name
/// (<c>"FileStorageUnavailableException"</c>) cannot.</item>
/// <item>Every count column is a number. A count cannot re-identify anyone by construction.</item>
/// </list>
/// <see cref="Ago.Chat.Architecture.Tests"/>'s own erasure-record test asserts this positively, over a
/// real persisted row, not by reading this type's shape - see that test's own remarks for why a type
/// check would not be enough (a column added later without updating this note would pass a
/// shape-based test and still leak).
/// </summary>
internal sealed class ErasureRecordEntityConfiguration : IEntityTypeConfiguration<ErasureRecordEntity>
{
    public void Configure(EntityTypeBuilder<ErasureRecordEntity> builder)
    {
        builder.ToTable("erasure_records");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(e => e.Scope).HasColumnName("scope").IsRequired();
        // No HasOne<Site>()/HasForeignKey - see this file's own remarks and ErasureRecordEntity's for
        // why the absence is deliberate, not a gap.
        builder.Property(e => e.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site).IsRequired();
        builder.Property(e => e.RequestedBy).HasColumnName("requested_by").HasConversion(IdConverters.Operator).IsRequired();
        builder.Property(e => e.Status).HasColumnName("status").IsRequired();
        builder.Property(e => e.FailureReason).HasColumnName("failure_reason");
        builder.Property(e => e.RequestedAt).HasColumnName("requested_at");
        builder.Property(e => e.CompletedAt).HasColumnName("completed_at");
        builder.Property(e => e.MessagesDeleted).HasColumnName("messages_deleted").HasDefaultValue(0);
        builder.Property(e => e.AttachmentsDeleted).HasColumnName("attachments_deleted").HasDefaultValue(0);
        builder.Property(e => e.StorageObjectsDeleted).HasColumnName("storage_objects_deleted").HasDefaultValue(0);
        builder.Property(e => e.NotesDeleted).HasColumnName("notes_deleted").HasDefaultValue(0);
        builder.Property(e => e.TagsDeleted).HasColumnName("tags_deleted").HasDefaultValue(0);
        builder.Property(e => e.ContactDetailsDeleted).HasColumnName("contact_details_deleted").HasDefaultValue(0);
        builder.Property(e => e.ConversationsMarkedForErasure)
            .HasColumnName("conversations_marked_for_erasure").HasDefaultValue(0);
        builder.Property(e => e.IdentitiesDeleted).HasColumnName("identities_deleted").HasDefaultValue(0);

        // `ck_erasure_records_scope`/`ck_erasure_records_status`: the same "a CHECK constraint
        // backstops the enum at the storage level" reasoning SiteConfiguration's own widget-position
        // constraint gives - a stray value written by a future direct SQL statement (not through
        // ErasureRecordQuery) is rejected by Postgres, not merely by C# code nobody ran.
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_erasure_records_scope", "scope IN ('Conversation', 'Site')");
            t.HasCheckConstraint("ck_erasure_records_status", "status IN ('Pending', 'Failed', 'Completed')");
        });

        // No index: nothing queries this table by site_id or status today (this file's own remarks on
        // why there is no read path yet) - caching.md's own "never cache/never index ahead of a real
        // query" reasoning applies equally to an index no code issues.
    }
}
