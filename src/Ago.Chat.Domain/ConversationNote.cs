namespace Ago.Chat.Domain;

/// <summary>
/// `18-04`: an operator's private annotation on a <see cref="Conversation"/> - visible to the
/// operator team, never to the visitor. Deliberately <b>not</b> a <see cref="Message"/> with a
/// <c>Kind</c> discriminator and deliberately <b>not</b> a child collection of <see cref="Conversation"/>
/// itself - its own standalone entity, its own table, reached only through <c>INoteRepository</c>.
///
/// <para><b>Why this is the whole point of the item.</b> <c>GetConversationHistoryHandler</c>'s visitor
/// entry point and its operator entry point call the exact same
/// <c>IConversationReadStore.GetHistoryAsync</c> method, over the exact same <c>messages</c> table, with
/// no <c>Kind</c>/author-scoped predicate anywhere in that query (<c>ConversationReadStore</c>'s own
/// remarks: "the tenant is reachable only through <c>conversations</c>" - there is no per-row
/// visibility filter at all, because none has ever been needed). A note stored as a
/// <c>messages</c> row would therefore need a filter <em>added</em> to that one shared query to keep it
/// from a visitor - filtered instead of structurally absent, which is exactly the "the boundary cannot
/// rest on the console remembering to filter" failure mode `18-04`'s own backlog item names. Keeping
/// <see cref="ConversationNote"/> out of <c>Message</c>/<c>Conversation</c> entirely - a separate CLR
/// type, a separate table, a separate repository interface with no method in common with
/// <c>IConversationRepository</c>/<c>IConversationReadStore</c> - means there is no predicate to forget:
/// nothing in <c>GetConversationHistoryHandler</c>'s call graph can reach this type at all, checked by
/// <c>NoteLeakProofTests</c> against the real Postgres-backed store.</para>
///
/// <para><b>Why not a child collection of <see cref="Conversation"/>, the way <see cref="Message"/> is.</b>
/// <see cref="Conversation"/>'s own repository (<c>ConversationRepository.GetByIdAsync</c>) loads the
/// whole aggregate, messages included, on every write path that touches a conversation - sending a
/// message, closing it, transferring it. Folding notes into that same aggregate would mean every one of
/// those loads also materialises every note ever left on the conversation, for zero benefit to any of
/// them, and would put a note-shaped property one property-access away from a future DTO mapper that
/// forgets to leave it out - the same "adjacent enough to copy from without noticing" risk the
/// separate-table decision above exists to close off entirely. A note has its own natural transaction
/// boundary (added by one operator, at one moment, independent of any message send) - the same
/// "does this change independently, in its own transaction" test <see cref="WebhookEndpoint"/>'s own
/// remarks apply to justify its split from <see cref="Site"/>.</para>
/// </summary>
public sealed class ConversationNote
{
    // A bound, not a product requirement - the same "an operator can add real context, not write an
    // essay" reasoning `CannedResponse.MaxBodyLength` gives for reusing an existing invariant rather
    // than guessing a second number. A note is never rendered as a message body, so MessageBody's own
    // limit is not the natural anchor here; kept smaller and stated as its own number instead.
    public const int MaxBodyLength = 4000;

    public ConversationNoteId Id { get; }

    public ConversationId ConversationId { get; }

    /// <summary>The operator who wrote it - author attribution the backlog item names explicitly
    /// ("author and timestamp recorded"). Never a <see cref="VisitorId"/>: a visitor has no route to
    /// this type at all, so there is nothing to distinguish it from.</summary>
    public OperatorId AuthorId { get; }

    public string Body { get; } = string.Empty;

    public DateTimeOffset CreatedAt { get; }

    private ConversationNote(
        ConversationNoteId id, ConversationId conversationId, OperatorId authorId, string body, DateTimeOffset createdAt)
    {
        Id = id;
        ConversationId = conversationId;
        AuthorId = authorId;
        Body = body;
        CreatedAt = createdAt;
    }

    // EF Core materialization only (1-04's precedent) - never called by domain code.
    private ConversationNote()
    {
    }

    /// <summary>
    /// Validation lives here, not in the handler - unlike <see cref="WebhookEndpoint.Register"/>'s URL
    /// legality (a policy check needing Infrastructure-shaped context, deliberately left to
    /// Application), an empty-or-oversized note body is a plain Domain invariant with nothing external
    /// to consult, the same split <see cref="MessageBody"/>/<see cref="CannedResponse"/> already use for
    /// themselves.
    /// </summary>
    public static ConversationNote Write(
        ConversationNoteId id, ConversationId conversationId, OperatorId authorId, string body, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ArgumentException("A note cannot be empty.", nameof(body));
        }

        var trimmed = body.Trim();
        if (trimmed.Length > MaxBodyLength)
        {
            throw new ArgumentException($"A note cannot exceed {MaxBodyLength} characters.", nameof(body));
        }

        return new ConversationNote(id, conversationId, authorId, trimmed, now);
    }
}
