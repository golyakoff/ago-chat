using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `23-03`: `conversation_assignments` - see <see cref="ConversationAssignmentInterval"/>'s own
/// remarks for why this is its own table rather than a child collection of <see cref="Conversation"/>.
/// </summary>
internal sealed class ConversationAssignmentIntervalConfiguration : IEntityTypeConfiguration<ConversationAssignmentInterval>
{
    public void Configure(EntityTypeBuilder<ConversationAssignmentInterval> builder)
    {
        // The closed-vocabulary backstop, the same reasoning ConversationConfiguration's own
        // ck_conversations_outcome constraint states: anything enforcing a guarantee ConversationAssignmentSource
        // already makes gets a constraint too, not just application code. `23-04`: widened to three -
        // Taken's own first real writer (AssignConversationHandler) landed without this constraint
        // widened alongside it in the same wave (reported rather than worked around, adr/0105's own
        // Alternatives considered), so this statement and Taken's first writer were, for one wave,
        // genuinely out of step - fixed here, in the next available migration slot. `23-05`: widened
        // again to four - Additional's own first real writers (SkipLockedAssignmentClaimer's and
        // RedisLockAssignmentClaimer's second pass) land in the same wave as this statement, unlike
        // Taken's own history above.
        builder.ToTable("conversation_assignments", t =>
        {
            t.HasCheckConstraint("ck_conversation_assignments_source", "source IN ('Assigned', 'Transferred', 'Taken', 'Additional')");
        });
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id").HasConversion(IdConverters.ConversationAssignment).ValueGeneratedNever();
        builder.Property(a => a.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site);
        builder.Property(a => a.ConversationId).HasColumnName("conversation_id").HasConversion(IdConverters.Conversation);
        builder.Property(a => a.OperatorId).HasColumnName("operator_id").HasConversion(IdConverters.Operator);
        builder.Property(a => a.StartedAt).HasColumnName("started_at");
        builder.Property(a => a.EndedAt).HasColumnName("ended_at");
        builder.Property(a => a.Source).HasColumnName("source").HasConversion<string>();

        // `site_id`/`operator_id` cascade the same way every other per-tenant table does
        // (data-model.md's "every table holding a tenant's data cascades from sites") - neither a site
        // nor an operator is ever hard-deleted by anything this codebase runs (operators are
        // soft-removed via `RemovedAt`, `13-03`), so this FK exists for referential integrity and is
        // never actually the thing that fires.
        builder.HasOne<Site>().WithMany().HasForeignKey(a => a.SiteId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Operator>().WithMany().HasForeignKey(a => a.OperatorId).OnDelete(DeleteBehavior.Cascade);

        // `conversation_id` deliberately carries NO foreign key - the one real decision `23-03`'s own
        // Scope left open ("whether 16-02's conversation erasure drains this table"). `16-02`'s
        // ConversationErasureQuery.DeleteConversationAsync removes the conversation row itself, not
        // just its content, and `decisions.md` §2's own amendment is explicit about the consequence
        // that must survive that: "erasing a conversation need not take last month's numbers with it."
        // A cascading FK would delete every interval the moment its conversation is erased - silently
        // reversing that decision the instant erasure ships, for a column with no content and no
        // visitor-identifying data of its own (an operator id and a conversation id, both already
        // opaque, plus timestamps - decisions.md §2's own "timestamps are not personal data" line).
        // So this table answers "drains it? No" by construction: a row can outlive the conversation it
        // names, on purpose, the same way `messages.content`/`attachments.message_id` already choose no
        // FK where the codebase wants a reference that may legitimately not resolve
        // (`data-model.md`'s "Keys and indexes"). personal-data.md carries this decision in prose.
        //
        // No plain index on ConversationId either - "an index arrives with its first real reader"
        // (data-model.md), and the one real reader today is CloseOpenAsync, which only ever wants the
        // *open* row. The partial index immediately below serves that read; a full-history-by-
        // conversation query has no caller yet to justify a second one.

        // The one query every Close writer needs: "the interval currently open for this conversation."
        // Unique, not merely indexed - a conversation has at most one operator at a time, so it has at
        // most one open interval, and this is that invariant enforced at the storage level rather than
        // only asserted by callers that remember to check first (ConversationConfiguration's own
        // check-constraint precedent, applied to an index instead of a CHECK because the rule spans
        // rows, not one row's own columns).
        builder.HasIndex(a => a.ConversationId).HasDatabaseName("ix_conversation_assignments_open")
            .IsUnique().HasFilter("ended_at IS NULL");

        // `23-03`'s own overlap-query proof (ConversationAssignmentOverlapQuery, tested with no real
        // caller yet - see that class's own remarks): "how many rows for this operator overlap instant
        // T" filters on operator_id and ranges on started_at, so this composite serves the query's own
        // leading predicates. Unmeasured beyond that - CLAUDE.md rule 7 forbids a performance claim
        // nobody ran a number for, and none has been run since nothing calls this query yet.
        builder.HasIndex(a => new { a.OperatorId, a.StartedAt }).HasDatabaseName("ix_conversation_assignments_operator_started");
    }
}
