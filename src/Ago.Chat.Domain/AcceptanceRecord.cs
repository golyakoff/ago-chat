namespace Ago.Chat.Domain;

/// <summary>
/// `24-01`: the fact that a subject agreed to a specific version of a specific document, at a specific
/// instant - the record every other item in Stage 24 ends with someone producing. An acceptance
/// nobody can reconstruct is indistinguishable from one that never happened (this item's own backlog
/// brief), so this type exists to make reconstruction possible without asking the person to take AGO's
/// word for it.
///
/// <para><b>Immutable, insert-only, no <c>Rename</c>/<c>Update</c>-shaped method at all.</b> Unlike
/// <see cref="ConversationNote"/> (also insert-only, for a different reason), an acceptance is not
/// merely "not edited in practice" - it is not editable even in principle, because the whole value of
/// the record is that it reflects the state of the world at the moment it was written. A second
/// acceptance of the same document by the same subject is a <em>second row</em>
/// (<see cref="IAcceptanceRepository.SaveAsync"/> only ever inserts), never an update to the first -
/// this is what keeps "what did they agree to in March" answerable after a later acceptance in June:
/// there is nothing in this type or its repository that could overwrite March's own row.</para>
///
/// <para><b>The document itself is out of scope, and this type reflects that structurally.</b>
/// <see cref="DocumentKey"/>/<see cref="DocumentVersion"/> are opaque, bounded strings - not a foreign
/// key to a documents table, because no such table exists yet (`24-02`'s own job) and this item must
/// not invent one speculatively. Whatever `24-02` builds, an acceptance only ever needs to *name* a
/// version, never to join against it from here - the read direction ("what does version 4 say") runs
/// from a person to `24-02`'s own published surface, not from this record.</para>
///
/// <para><b>Request context: enough to be credible, not a surveillance log - the item's own line, and
/// the reasoning per field.</b> <see cref="ClientIp"/> and <see cref="UserAgent"/> are the two facts a
/// consent record commonly needs to be defensible ("this was submitted from this address, by this
/// client") without widening into the thing `personal-data.md` already warns against elsewhere in this
/// system. Deliberately <b>not</b> captured, and said here so a later change has to argue with a
/// written decision rather than merely fail to notice one: no referrer or landing-page URL
/// (<see cref="Conversation.Source"/> already owns "how did this visitor arrive," and duplicating it
/// here would be the same data reappearing in a second store for no reader); no session or correlation
/// id (traceable through the surrounding request's own telemetry if it is ever needed, `personal-data.md`'s
/// own Traces row); no device fingerprint, no geolocation, no copy of the document's text. Each of
/// those would answer a question nobody has asked this record to answer.</para>
/// </summary>
public sealed class AcceptanceRecord
{
    // Bounds chosen the same way ConversationNote.MaxBodyLength states its own number: generous enough
    // for a real value, far too small to become a place someone pastes something else.
    public const int MaxDocumentKeyLength = 200;
    public const int MaxDocumentVersionLength = 100;
    public const int MaxUserAgentLength = 512;
    public const int MaxClientIpLength = 45; // the longest textual IPv6 representation.

    public AcceptanceRecordId Id { get; }

    public AcceptanceSubjectKind SubjectKind { get; }

    /// <summary>The subject's own id, widened to <see cref="Guid"/> - see
    /// <see cref="AcceptanceSubjectKind"/>'s own remarks for why a bare Guid plus a kind, rather than
    /// three nullable strongly-typed columns. Deliberately carries <b>no foreign key</b> to
    /// `sites`/`operators`/`visitors` in the persistence configuration - see this type's own erasure
    /// remarks below.</summary>
    public Guid SubjectId { get; }

    public string DocumentKey { get; } = string.Empty;

    public string DocumentVersion { get; } = string.Empty;

    public DateTimeOffset AcceptedAt { get; }

    /// <summary>The request's own client address at the moment of acceptance, textual (IPv4 or IPv6) -
    /// nullable because a caller that cannot supply one (a background-driven acceptance, if `24-03`
    /// ever needs one) must not be forced to invent a value.</summary>
    public string? ClientIp { get; }

    public string? UserAgent { get; }

    private AcceptanceRecord(
        AcceptanceRecordId id, AcceptanceSubjectKind subjectKind, Guid subjectId, string documentKey,
        string documentVersion, DateTimeOffset acceptedAt, string? clientIp, string? userAgent)
    {
        Id = id;
        SubjectKind = subjectKind;
        SubjectId = subjectId;
        DocumentKey = documentKey;
        DocumentVersion = documentVersion;
        AcceptedAt = acceptedAt;
        ClientIp = clientIp;
        UserAgent = userAgent;
    }

    // EF Core materialization only (1-04's precedent) - never called by domain code.
    private AcceptanceRecord()
    {
    }

    /// <summary>The tenant accepted - see <see cref="AcceptanceSubjectKind.Tenant"/>'s own remarks for
    /// why a <see cref="SiteId"/> is the right id type for this kind.</summary>
    public static AcceptanceRecord ForTenant(
        AcceptanceRecordId id, SiteId tenantId, string documentKey, string documentVersion, DateTimeOffset acceptedAt,
        string? clientIp = null, string? userAgent = null) =>
        Accept(id, AcceptanceSubjectKind.Tenant, tenantId.Value, documentKey, documentVersion, acceptedAt, clientIp, userAgent);

    public static AcceptanceRecord ForOperator(
        AcceptanceRecordId id, OperatorId operatorId, string documentKey, string documentVersion, DateTimeOffset acceptedAt,
        string? clientIp = null, string? userAgent = null) =>
        Accept(id, AcceptanceSubjectKind.Operator, operatorId.Value, documentKey, documentVersion, acceptedAt, clientIp, userAgent);

    public static AcceptanceRecord ForVisitor(
        AcceptanceRecordId id, VisitorId visitorId, string documentKey, string documentVersion, DateTimeOffset acceptedAt,
        string? clientIp = null, string? userAgent = null) =>
        Accept(id, AcceptanceSubjectKind.Visitor, visitorId.Value, documentKey, documentVersion, acceptedAt, clientIp, userAgent);

    /// <summary>
    /// The one real constructor every <c>For*</c> factory funnels through - validation lives here,
    /// not in a handler, the same split <see cref="ConversationNote.Write"/>'s own remarks give for a
    /// plain invariant with nothing external to consult.
    /// </summary>
    private static AcceptanceRecord Accept(
        AcceptanceRecordId id, AcceptanceSubjectKind subjectKind, Guid subjectId, string documentKey,
        string documentVersion, DateTimeOffset acceptedAt, string? clientIp, string? userAgent)
    {
        if (subjectId == Guid.Empty)
        {
            throw new ArgumentException("A subject id cannot be empty.", nameof(subjectId));
        }

        if (string.IsNullOrWhiteSpace(documentKey))
        {
            throw new ArgumentException("A document key cannot be empty.", nameof(documentKey));
        }

        var trimmedKey = documentKey.Trim();
        if (trimmedKey.Length > MaxDocumentKeyLength)
        {
            throw new ArgumentException($"A document key cannot exceed {MaxDocumentKeyLength} characters.", nameof(documentKey));
        }

        if (string.IsNullOrWhiteSpace(documentVersion))
        {
            throw new ArgumentException("A document version cannot be empty.", nameof(documentVersion));
        }

        var trimmedVersion = documentVersion.Trim();
        if (trimmedVersion.Length > MaxDocumentVersionLength)
        {
            throw new ArgumentException(
                $"A document version cannot exceed {MaxDocumentVersionLength} characters.", nameof(documentVersion));
        }

        if (clientIp is { Length: > MaxClientIpLength })
        {
            throw new ArgumentException($"A client IP cannot exceed {MaxClientIpLength} characters.", nameof(clientIp));
        }

        if (userAgent is { Length: > MaxUserAgentLength })
        {
            throw new ArgumentException($"A user agent cannot exceed {MaxUserAgentLength} characters.", nameof(userAgent));
        }

        return new AcceptanceRecord(id, subjectKind, subjectId, trimmedKey, trimmedVersion, acceptedAt, clientIp, userAgent);
    }
}
