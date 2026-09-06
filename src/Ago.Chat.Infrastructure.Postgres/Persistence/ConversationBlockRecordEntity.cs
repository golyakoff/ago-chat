using Ago.Chat.Domain;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `24-10`: exists solely so `dotnet ef migrations add` can generate `conversation_block_records`' own
/// `CREATE TABLE` - the same "migration-scaffolding only, nothing ever queries this DbSet" shape
/// <see cref="ErasureRecordEntity"/>/<see cref="AccessRecordEntity"/>'s own remarks give in full.
/// <see cref="ConversationBlockRepository"/> is raw Npgsql end to end, for the identical reason those
/// two give: a receipt with no aggregate behind it has nothing an EF change-tracked load-mutate-save
/// buys it, and this table is never mutated after insert - a block and its later reversal are two rows,
/// never one row updated in place (<see cref="Domain.ConversationBlockRecordKind"/>'s own remarks).
///
/// <para><b>A real FK to <c>conversations</c>, unlike <see cref="ErasureRecordEntity"/>/
/// <see cref="AccessRecordEntity"/>.</b> Those two carry none, deliberately, so their receipts survive
/// the very row deletion they are evidence of. This table has no equivalent reason to survive: it is
/// evidence about a conversation's *current, reversible* operational state, not about an erasure that
/// already happened, and once the conversation itself is genuinely erased there is nothing left for a
/// "was this ever blocked" question to be worth answering (`ConversationBlockRecordEntityConfiguration`'s
/// own remarks). Cascade delete is therefore the primary mechanism here, not merely a backstop the way
/// it is for <c>conversation_notes</c>.</para>
/// </summary>
internal sealed class ConversationBlockRecordEntity
{
    public Guid Id { get; set; }

    // ConversationId (the value object), not a bare Guid - this is a real FK to `conversations`
    // (unlike ErasureRecordEntity/AccessRecordEntity's own deliberately-absent ones), and EF's
    // relationship needs its foreign key's CLR type to match Conversation.Id's own type.
    public ConversationId ConversationId { get; set; }

    public Guid SiteId { get; set; }

    public string Kind { get; set; } = string.Empty;

    public Guid ActorId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}
