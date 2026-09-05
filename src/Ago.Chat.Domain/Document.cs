namespace Ago.Chat.Domain;

/// <summary>
/// `24-02`: one row per document key - the aggregate root that owns the ordering of every
/// <see cref="PublishedDocumentVersion"/> published under that key, the identical role
/// <see cref="Conversation"/> plays for its own <see cref="Message"/>s. A second table rather than a
/// counter column bolted onto <see cref="PublishedDocumentVersion"/> itself, for the same reason
/// `Conversation`/`Message` are two tables rather than one self-referencing one: the counter
/// (<see cref="LastSequence"/>) and optimistic-concurrency token belong to the *document*, not to any
/// one version, and every publish needs to read and increment that shared value under one lock -
/// exactly what <see cref="Conversation.LastSequence"/>'s own remarks describe for a message send.
///
/// <para><b>Concurrency: the identical `xmin`/retry shape `6-08` established for `Conversation`.</b> Two
/// concurrent publishes for the same key must not both compute <c>LastSequence + 1</c> from the same
/// stale read and collide - <see cref="IDocumentRepository.SaveAsync"/>'s own remarks (and
/// <see cref="DocumentConcurrencyConflictException"/>'s) describe the translated exception a handler
/// catches and retries against a freshly reloaded aggregate, the same shape
/// <see cref="ConversationConcurrencyConflictException"/> already established. In practice this row is
/// written by exactly one caller (the platform owner, `24-02`'s own "named owner" - `OwnerDocumentEndpoints`),
/// so contention is not a load concern here the way it is for `Conversation`; the mechanism is reused
/// because it already exists and is already proven, not because this table needs to survive real
/// concurrent write traffic.</para>
///
/// <para><b>Never deleted, and nothing in this codebase deletes a <see cref="PublishedDocumentVersion"/>
/// either.</b> `24-02`'s own Done-when: "a superseded version is still readable, and a test proves
/// publishing a new one does not remove it." <see cref="Publish"/> only ever appends - there is no
/// method on this type that removes a <see cref="PublishedDocumentVersion"/> from <see cref="Versions"/>,
/// the structural half of that guarantee (<see cref="AcceptanceRecord"/>'s own "no delete method" shape,
/// restated here for a different kind of evidence).</para>
/// </summary>
public sealed class Document
{
    public const int MaxDocumentKeyLength = PublishedDocumentVersion.MaxDocumentKeyLength;

    public DocumentId Id { get; }

    public string DocumentKey { get; } = string.Empty;

    /// <summary>The last <see cref="PublishedDocumentVersion.Sequence"/> handed out for this document -
    /// the counter <see cref="Publish"/> increments before minting the next version, the same role
    /// <see cref="Conversation.LastSequence"/> plays for <see cref="Message.Sequence"/>.</summary>
    public int LastSequence { get; private set; }

    private readonly List<PublishedDocumentVersion> _versions = [];

    /// <summary>Every version ever published under this key, oldest first - small and bounded (a
    /// document changes a handful of times over the life of this product, not thousands), the same
    /// "plain unbounded list" shape <see cref="IAcceptanceRepository.GetForSubjectAsync"/>'s own remarks
    /// already accept for an identically-bounded collection.</summary>
    public IReadOnlyList<PublishedDocumentVersion> Versions => _versions;

    /// <summary>The most recently published version - the one `24-02`'s "published surface" serves
    /// when nobody asks for a specific version by name. <see langword="null"/> only for a
    /// <see cref="Document"/> that exists with no version published under it yet, which
    /// <see cref="IDocumentRepository.GetByKeyAsync"/>'s own contract never actually hands a caller
    /// (a document row is created by <see cref="Publish"/> in the same save as its first version).</summary>
    public PublishedDocumentVersion? Current => _versions.Count == 0 ? null : _versions[^1];

    private Document(DocumentId id, string documentKey)
    {
        Id = id;
        DocumentKey = documentKey;
    }

    // EF Core materialization only (1-04's precedent) - never called by domain code.
    private Document()
    {
    }

    /// <summary>A brand-new document, no version published yet - <see cref="IDocumentRepository"/>'s own
    /// remarks explain why a caller only ever sees this immediately before calling <see cref="Publish"/>
    /// on it, never as a standalone row with nothing readable behind it.</summary>
    public static Document Create(DocumentId id, string documentKey)
    {
        if (string.IsNullOrWhiteSpace(documentKey))
        {
            throw new ArgumentException("A document key cannot be empty.", nameof(documentKey));
        }

        var trimmed = documentKey.Trim();
        if (trimmed.Length > MaxDocumentKeyLength)
        {
            throw new ArgumentException($"A document key cannot exceed {MaxDocumentKeyLength} characters.", nameof(documentKey));
        }

        // Lower-kebab-case only - this string reaches an unauthenticated HTTP route segment verbatim
        // (`GET /api/v1/documents/{documentKey}`), the same charset discipline `ModuleKey`'s own
        // validation applies for the identical reason (a route segment, not free text).
        if (!IsValidKey(trimmed))
        {
            throw new ArgumentException(
                "A document key must be lowercase letters, digits and single hyphens (e.g. 'privacy-policy').",
                nameof(documentKey));
        }

        return new Document(id, trimmed);
    }

    /// <summary>Mints and appends the next <see cref="PublishedDocumentVersion"/> - the only way one is
    /// ever created. Increments <see cref="LastSequence"/> first, so the new version's own
    /// <see cref="PublishedDocumentVersion.Sequence"/> is always strictly greater than every version
    /// already in <see cref="Versions"/>, which is what keeps <see cref="Current"/> correct as
    /// "the last element" rather than a value this method has to search for.</summary>
    public PublishedDocumentVersion Publish(
        PublishedDocumentVersionId versionId, string title, string body, DateTimeOffset publishedAt)
    {
        LastSequence++;
        var version = PublishedDocumentVersion.Create(versionId, Id, DocumentKey, LastSequence, title, body, publishedAt);
        _versions.Add(version);
        return version;
    }

    private static bool IsValidKey(string key)
    {
        if (key.Length == 0 || key[0] == '-' || key[^1] == '-')
        {
            return false;
        }

        var previousWasHyphen = false;
        foreach (var c in key)
        {
            if (c == '-')
            {
                if (previousWasHyphen)
                {
                    return false;
                }

                previousWasHyphen = true;
                continue;
            }

            previousWasHyphen = false;
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)))
            {
                return false;
            }
        }

        return true;
    }
}
