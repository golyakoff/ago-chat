namespace Ago.Chat.Domain;

/// <summary>
/// `14-14`/`adr/0079` section 6: a phone number, email address, or other contact fact an operator
/// typed because a visitor said it out loud - never because AGO Chat received a real message from
/// that address, and never because a verification code proved anyone controls it. A small, honest
/// reference note for the operator's own use, not a routable identity.
///
/// <para><b>Why this is not a <see cref="ChannelIdentity"/>, restated in this type's own words (not
/// copied from the ADR).</b> <see cref="ChannelIdentity"/>'s entire reason to exist is that its
/// <see cref="ChannelIdentity.VisitorId"/> link is trustworthy enough to route a reply through -
/// <see cref="ChannelIdentity.Link"/> is only ever called from evidence: a real inbound message
/// (`ReceiveChannelMessageHandler`) or, since `14-12`, a verified confirmation code the visitor typed
/// back into the very channel being linked. Every field on that type exists to answer "is this address
/// still safe to send to" - <see cref="ChannelIdentity.Active"/>, <see cref="ChannelIdentity.LastSeenAt"/>,
/// the unique (site, kind, address) index that stops two visitors from silently sharing one inbox. A
/// <see cref="VisitorContactDetail"/> answers a completely different question - "what did an operator
/// write down" - and has no adapter to prove any of it: for the only channels this system cannot yet
/// call ("SMS"/"14-03", "Email"/"14-09" — both unbuilt), there is no inbound message that could ever
/// arrive to confirm a number even exists. Giving this type <see cref="ChannelIdentity"/>'s shape
/// (an active flag, a last-seen timestamp, a uniqueness constraint) would dress up a guess as
/// evidence and invite exactly the failure `adr/0079` section 6 names: the value silently becoming a
/// real send target the day a matching adapter finally ships, because nothing in the type itself ever
/// forced a re-think at that point. Keeping the two types structurally unrelated - no shared base
/// type, no shared repository interface, no field either one reads from the other - means there is no
/// code path for that promotion to happen by accident; it would have to be a deliberate new write path
/// someone adds on purpose, the same "structurally incapable, not filtered" standard
/// <see cref="ConversationNote"/>'s own remarks hold <em>its</em> separation to.</para>
///
/// <para><b>Why its own aggregate rather than a value object on <see cref="Visitor"/>.</b> The backlog
/// item is explicit that a visitor may hold more than one - a personal number and a work number both
/// worth keeping - so this needs to be a collection either way; making it its own row with its own id
/// is the same "many-to-one, not embedded" reasoning <see cref="ChannelIdentity"/>'s own remarks give
/// for point (1) of its own aggregate case, applied to a type that additionally never needs
/// <see cref="Visitor"/>'s own write lock to record.</para>
///
/// <para><b>No <c>Active</c>/soft-delete flag, unlike <see cref="ChannelIdentity"/>/<see cref="ChannelCredential"/>.</b>
/// Those two keep a terminal, non-deleted row after unlinking/revoking because "this address stopped
/// being valid" is itself a fact worth remembering - the row's own past existence had consequences
/// (messages really were routed through it). A mistyped phone number an operator deletes seconds after
/// noticing the typo has no such history to protect; the backlog item calls this "delete a mistaken
/// entry" in exactly those words, so deletion (<c>IVisitorContactDetailRepository.DeleteAsync</c>) is a
/// real row removal, not a state flip - there is no <c>Delete</c>/<c>Unlink</c>-shaped method on this
/// type at all, because nothing downstream needs to ask "did this visitor ever have a contact detail
/// that got removed."</para>
/// </summary>
public sealed class VisitorContactDetail
{
    // A bound, not a product requirement - the same "an operator can record a real fact, not write an
    // essay" reasoning `ConversationNote.MaxBodyLength` gives for stating its own number instead of
    // reusing `MessageBody.MaxLength` verbatim. A phone number, an email address, or a short "work
    // mobile" annotation next to one never approaches even `ConversationNote`'s own 4000-character
    // note-sized limit, so this is smaller still - generous enough for a value plus a short label, far
    // too small to become a place someone pastes a document.
    public const int MaxValueLength = 500;

    public VisitorContactDetailId Id { get; }

    public VisitorId VisitorId { get; }

    public VisitorContactDetailKind Kind { get; }

    public string Value { get; } = string.Empty;

    /// <summary>
    /// `23-09`: nullable since this widening - a visitor-supplied detail (<see cref="Source"/> ==
    /// <see cref="VisitorContactDetailSource.Visitor"/>) has no operator behind it at all, never a
    /// placeholder id. Every reader that existed before this item assumed a present value; the two
    /// this codebase has (<c>ListVisitorContactDetailsHandler</c>'s DTO and the console's own panel)
    /// are updated in this same change to render a null as "the visitor themselves," never as an
    /// empty cell or a fabricated name - see those files' own remarks.
    /// </summary>
    public OperatorId? RecordedByOperatorId { get; }

    /// <summary>`23-09`. See <see cref="VisitorContactDetailSource"/>'s own remarks for why this is a
    /// real column rather than inferred from <see cref="RecordedByOperatorId"/> being null.</summary>
    public VisitorContactDetailSource Source { get; }

    /// <summary>
    /// `23-09`/`docs/design/decisions.md` §4: never proven by a code the visitor typed back, never
    /// proven by a real inbound message - the same evidentiary gap <see cref="VisitorContactDetail"/>'s
    /// own class remarks already name as the whole reason this type is not a <see cref="ChannelIdentity"/>.
    /// Always <see langword="false"/> for everything this item's two factory methods produce, on
    /// either <see cref="Source"/>: an operator relaying what a visitor said out loud is exactly as
    /// unverified as the visitor typing it themselves through the unverified control this item builds
    /// (`decisions.md` §4's "no verification for a callback" - verification is paid for by whoever
    /// benefits, and nothing here is a booking). There is deliberately no method on this type that ever
    /// sets it <see langword="true"/> - the verified mode's own caller is out of this item's scope, and
    /// adding a way to flip this flag with nothing yet using it would be exactly the premature
    /// generalisation `clean-architecture.md` warns against.
    /// </summary>
    public bool Verified { get; }

    public DateTimeOffset RecordedAt { get; }

    private VisitorContactDetail(
        VisitorContactDetailId id, VisitorId visitorId, VisitorContactDetailKind kind, string value,
        OperatorId? recordedByOperatorId, VisitorContactDetailSource source, bool verified, DateTimeOffset recordedAt)
    {
        Id = id;
        VisitorId = visitorId;
        Kind = kind;
        Value = value;
        RecordedByOperatorId = recordedByOperatorId;
        Source = source;
        Verified = verified;
        RecordedAt = recordedAt;
    }

    // EF Core materialization only (1-04's precedent) - never called by domain code.
    private VisitorContactDetail()
    {
    }

    private static string ValidateAndTrim(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A contact detail cannot be empty.", nameof(value));
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxValueLength)
        {
            throw new ArgumentException($"A contact detail cannot exceed {MaxValueLength} characters.", nameof(value));
        }

        return trimmed;
    }

    /// <summary>
    /// An operator, mid-conversation, writing down a fact a visitor just said - `14-14`'s original and
    /// only path until `23-09` added <see cref="RecordFromVisitor"/> beside it. Validation lives here,
    /// not in the handler - the same split <see cref="MessageBody"/>/<see cref="ConversationNote"/>
    /// already use for themselves: an empty-or-oversized value is a plain Domain invariant with
    /// nothing external to consult (no adapter to ask, no format to validate against a provider - this
    /// item's own Out-of-scope rules that out explicitly).
    /// </summary>
    public static VisitorContactDetail Record(
        VisitorContactDetailId id, VisitorId visitorId, VisitorContactDetailKind kind, string value,
        OperatorId recordedByOperatorId, DateTimeOffset now) =>
        new(id, visitorId, kind, ValidateAndTrim(value), recordedByOperatorId, VisitorContactDetailSource.Operator,
            verified: false, now);

    /// <summary>
    /// `23-09`: the visitor's own control, reached through a visitor-authenticated write path -
    /// see <c>RecordVisitorContactDetailHandler.HandleAsVisitorAsync</c>'s own remarks for the
    /// authorization shape, which is deliberately not this type's own concern (the identical
    /// "validation is a Domain invariant, authorization is the handler's job" split <see cref="Record"/>
    /// above already draws). No <see cref="OperatorId"/> parameter exists on this factory at all -
    /// there is no operator behind a visitor's own submission, ever, not merely an unset one.
    /// </summary>
    public static VisitorContactDetail RecordFromVisitor(
        VisitorContactDetailId id, VisitorId visitorId, VisitorContactDetailKind kind, string value,
        DateTimeOffset now) =>
        new(id, visitorId, kind, ValidateAndTrim(value), recordedByOperatorId: null, VisitorContactDetailSource.Visitor,
            verified: false, now);
}
