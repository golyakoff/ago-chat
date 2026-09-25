namespace Ago.Chat.Domain;

/// <summary>
/// `adr/0184` (author decision O3): an operator's private annotation about a <em>person</em> - "pays
/// late", "prefers afternoons" - attached to the account's Person (<see cref="Visitor"/>) rather than to
/// any one conversation. This is the note the calendar's <c>customers.notes</c> column used to hold;
/// that copy is deleted, and chat, as the account's single person registry, is where the note lives
/// now, so a console showing a booking and a console showing a dialogue read the same text.
///
/// <para><b>Deliberately a second note type beside <see cref="ConversationNote"/>, not a widening of
/// it.</b> A conversation note is about one exchange and dies with it (`16-02`'s erasure drains it by
/// conversation); a person note is about the person and must survive any one conversation being
/// erased while that person is still the account's customer - it goes only when the person does. Two
/// lifetimes, two tables. Every other property matches <see cref="ConversationNote"/> on purpose:
/// append-only, never edited, bounded body, structurally unreachable from any visitor-facing read (the
/// same <c>NoteLeakProofTests</c> discipline applies).</para>
/// </summary>
public sealed class PersonNote
{
    /// <summary>The same bound <see cref="ConversationNote.MaxBodyLength"/> states, for the same reason
    /// - and the same 4000 the calendar's deleted <c>customers.notes</c> column carried, so nothing an
    /// operator could write there is refused here.</summary>
    public const int MaxBodyLength = 4000;

    public PersonNoteId Id { get; }

    /// <summary>The person this note is about - chat's own <see cref="Visitor"/> id, which is the
    /// account-scoped person id every product references (`adr/0184` decision 2).</summary>
    public VisitorId PersonId { get; }

    public OperatorId AuthorId { get; }

    public string Body { get; } = string.Empty;

    public DateTimeOffset CreatedAt { get; }

    private PersonNote(PersonNoteId id, VisitorId personId, OperatorId authorId, string body, DateTimeOffset createdAt)
    {
        Id = id;
        PersonId = personId;
        AuthorId = authorId;
        Body = body;
        CreatedAt = createdAt;
    }

    // EF Core materialization only (1-04's precedent) - never called by domain code.
    private PersonNote()
    {
    }

    public static PersonNote Write(
        PersonNoteId id, VisitorId personId, OperatorId authorId, string body, DateTimeOffset now)
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

        return new PersonNote(id, personId, authorId, trimmed, now);
    }
}
