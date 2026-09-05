namespace Ago.Chat.Domain;

/// <summary>
/// `24-02`: one published, immutable version of one document - the thing a person actually reads, and
/// the thing an <see cref="AcceptanceRecord"/> names. Constructed only through
/// <see cref="Document.Publish"/>, never directly - the same "the aggregate root is the one place a
/// child is created" shape <see cref="Conversation.AddVisitorMessage"/> already establishes for
/// <see cref="Message"/>. Insert-only, no <c>Rename</c>/<c>Update</c>-shaped method at all - the exact
/// reasoning <see cref="AcceptanceRecord"/>'s own remarks give: a version is evidence of what a reader
/// saw at a moment, and overwriting it in place would silently invalidate whatever
/// <see cref="AcceptanceRecord.DocumentVersion"/> already points at it.
///
/// <para><b><see cref="Version"/> is server-assigned from <see cref="Sequence"/>, never a caller's own
/// string.</b> `24-02`'s own Scope asks for an identifier that is "stable, ordered and human-quotable"
/// all at once - three properties a caller-supplied string cannot be trusted to hold together (nothing
/// stops two callers choosing the same label, or a label that sorts wrong). Deriving <c>"v{Sequence}"</c>
/// from a number <see cref="Document"/> hands out in strictly increasing order gets all three for
/// free: stable because it is never reassigned once written, ordered because it *is* the order
/// (CLAUDE.md rule 11 - "ordering never depends on a clock... it uses the server-assigned sequence" -
/// the identical reasoning <see cref="Message.Sequence"/> already applies, restated here for a document
/// instead of a message), and human-quotable because "you accepted v4 on 12 March" is exactly the
/// sentence `24-02`'s own Scope names.</para>
///
/// <para><b><see cref="DocumentKey"/> is denormalised onto this row, not reached through
/// <see cref="DocumentId"/> alone.</b> The public, unauthenticated read path (`24-02`'s own published
/// surface) looks a version up by <c>(documentKey, version)</c> - the caller has never seen a
/// <see cref="DocumentId"/> and should not need to join through <see cref="Document"/> on every
/// anonymous request just to filter by the key it actually has. The same trade <see cref="Message.SiteId"/>'s
/// own remarks describe (`18-01`: a column that duplicates a value reachable through a parent, kept
/// anyway because the hot read path must not pay a join for it) applies here, for an even safer
/// reason: a version's own <see cref="DocumentKey"/> can never drift out of sync with its parent's,
/// because nothing ever moves a version to a different <see cref="Document"/> once published.</para>
/// </summary>
public sealed class PublishedDocumentVersion
{
    // The same bound AcceptanceRecord.DocumentKey/DocumentVersion already carry - not a coincidence:
    // 24-01's own column is exactly what a version identifier minted here must fit inside
    // (docs/backlog/24-02's own point: "your identifiers must be the ones those columns will hold").
    public const int MaxDocumentKeyLength = AcceptanceRecord.MaxDocumentKeyLength;
    public const int MaxVersionLength = AcceptanceRecord.MaxDocumentVersionLength;

    public const int MaxTitleLength = 200;

    // Generous enough for a real privacy policy or terms document (tens of thousands of words would
    // be unusual even for a verbose policy), far too small to become a place someone pastes an entire
    // book - the same "bound chosen by the shape of a real value, not measured" reasoning
    // ConversationNote.MaxBodyLength's own remarks give for a smaller bound on a smaller kind of text.
    public const int MaxBodyLength = 100_000;

    public PublishedDocumentVersionId Id { get; }

    public DocumentId DocumentId { get; }

    public string DocumentKey { get; } = string.Empty;

    /// <summary>Assigned by <see cref="Document.Publish"/> from <see cref="Document.LastSequence"/> -
    /// never supplied by a caller. The true ordering key; <see cref="Version"/> is this value's own
    /// human-facing spelling.</summary>
    public int Sequence { get; }

    /// <summary><c>"v{Sequence}"</c> - see this type's own remarks for why deriving it from
    /// <see cref="Sequence"/>, rather than accepting a caller's own string, is what makes it stable,
    /// ordered and human-quotable simultaneously.</summary>
    public string Version { get; } = string.Empty;

    public string Title { get; } = string.Empty;

    /// <summary>The document's own text - plain text or simple markdown, rendered by whatever reads
    /// this (`24-03`/`24-04`/`24-05`'s own consoles/widget, none of which this item builds). Carries no
    /// markup language of its own; deciding one is a future item's job, not this one's.</summary>
    public string Body { get; } = string.Empty;

    public DateTimeOffset PublishedAt { get; }

    private PublishedDocumentVersion(
        PublishedDocumentVersionId id, DocumentId documentId, string documentKey, int sequence, string version,
        string title, string body, DateTimeOffset publishedAt)
    {
        Id = id;
        DocumentId = documentId;
        DocumentKey = documentKey;
        Sequence = sequence;
        Version = version;
        Title = title;
        Body = body;
        PublishedAt = publishedAt;
    }

    // EF Core materialization only (1-04's precedent) - never called by domain code.
    private PublishedDocumentVersion()
    {
    }

    /// <summary>Internal: only <see cref="Document.Publish"/> may construct one - the same
    /// "aggregate root is the sole factory for its own child" shape
    /// <see cref="Conversation.AddVisitorMessage"/> already establishes for <see cref="Message"/>.
    /// <paramref name="sequence"/> is trusted as already-validated (positive, already incremented) by
    /// the caller; this method's own validation is for the caller-supplied strings only.</summary>
    internal static PublishedDocumentVersion Create(
        PublishedDocumentVersionId id, DocumentId documentId, string documentKey, int sequence, string title,
        string body, DateTimeOffset publishedAt)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("A document title cannot be empty.", nameof(title));
        }

        var trimmedTitle = title.Trim();
        if (trimmedTitle.Length > MaxTitleLength)
        {
            throw new ArgumentException($"A document title cannot exceed {MaxTitleLength} characters.", nameof(title));
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ArgumentException("A document body cannot be empty.", nameof(body));
        }

        var trimmedBody = body.Trim();
        if (trimmedBody.Length > MaxBodyLength)
        {
            throw new ArgumentException($"A document body cannot exceed {MaxBodyLength} characters.", nameof(body));
        }

        var version = $"v{sequence}";
        // Defensive only - unreachable for any sequence this codebase could ever actually reach
        // (MaxVersionLength is 100 characters; "v" plus an int is never close), kept so a future
        // change to the format string cannot silently write a value AcceptanceRecord.DocumentVersion
        // would then refuse.
        if (version.Length > MaxVersionLength)
        {
            throw new ArgumentException($"A document version cannot exceed {MaxVersionLength} characters.", nameof(sequence));
        }

        return new PublishedDocumentVersion(id, documentId, documentKey, sequence, version, trimmedTitle, trimmedBody, publishedAt);
    }
}
