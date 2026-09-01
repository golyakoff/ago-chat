namespace Ago.Chat.Domain;

/// <summary>
/// A single message within a <see cref="Conversation"/>. Constructed only through
/// <see cref="Conversation.AddVisitorMessage"/>/<see cref="Conversation.AddOperatorMessage"/>/<see
/// cref="Conversation.AddSystemMessage"/> - the internal constructor keeps every other assembly,
/// including <c>Ago.Chat.Application</c>, from creating one directly and bypassing the conversation's
/// own invariants.
/// </summary>
public sealed class Message
{
    public MessageId Id { get; }

    public ConversationId ConversationId { get; }

    public int Sequence { get; }

    public MessageAuthorKind AuthorKind { get; }

    public Guid AuthorId { get; }

    public MessageBody Body { get; }

    /// <summary>
    /// `18-01`: denormalized from the owning <see cref="Conversation.SiteId"/> at construction -
    /// <see cref="Conversation.AddMessage"/> is the only place that sets it, using its own aggregate's
    /// `SiteId`, never a value the caller supplies (adr/0031's Addendum: "a plain denormalized column
    /// ... populated at write time from the owning Conversation").
    ///
    /// <para><b>Non-nullable as of `15-09`/`adr/0087`.</b> Before this item, the column was nullable
    /// "for history" - a message written before `18-01` added it had no value, closed only by
    /// <c>MessageSiteIdBackfillJob</c>'s own slow, asynchronous convergence. `15-09`'s own
    /// repartitioning migration is a full create-copy-drop of this table (`PARTITION BY HASH (site_id)`
    /// requires every row to route to a bucket, and a query that forgets to filter `site_id` is the
    /// exact failure mode `adr/0087` exists to make expensive rather than invisible) - since the copy
    /// step already joins every row back to its owning `conversations` row for a defensive fallback
    /// (the same `COALESCE` shape `13-06`'s own migration used for `retention_class`), closing the
    /// historical gap for good cost nothing extra: every row gets a real, non-approximated `site_id`
    /// (an exact join key, not a guess, unlike `retention_class`'s own one-time tier approximation),
    /// and there is no live data this item had to preserve a nullable placeholder for
    /// (`adr/0087`'s own "no live clients, no data to migrate" premise). <c>MessageSiteIdBackfillJob</c>
    /// is deleted in the same change - its entire reason to exist was a gap this migration now closes
    /// once, structurally, rather than converging on slowly forever.</para>
    /// </summary>
    public SiteId SiteId { get; }

    /// <summary>`13-06`/`adr/0031`: the immutable half of this table's partition key, stamped from
    /// the owning <see cref="Conversation"/>'s site's <see cref="Site.Tier"/> at the moment this
    /// message is constructed - see <see cref="RetentionClass"/>'s own remarks for why it is not
    /// recomputed from anything read later. Unlike <see cref="SiteId"/> (nullable, absent on rows
    /// older than the column), this is never null: `13-06`'s own migration backfills a value for
    /// every existing row as part of its one-way rename/create/copy/drop (the physical column is
    /// `NOT NULL` because Postgres requires the full partition key on every row, not an optional
    /// one), and every row this aggregate constructs from here on always resolves one through
    /// <see cref="Conversation.AddMessage"/>. The mapping from tier to class for a row written
    /// before this column existed is a one-time approximation against whatever tier the site holds
    /// *today*, not whatever it held when the message was actually sent - stated here because
    /// nothing records the true historical value, the same honestly-named limitation
    /// `adr/0031`'s own Scope section states for the migration.</summary>
    public RetentionClass RetentionClass { get; }

    /// <summary>`5-07`: the client-generated id realtime.md's Client protocol section named as a
    /// design intent since `3-03` and left unwired - see <see cref="Conversation.AddMessage"/> for
    /// where it is actually used (retry-dedup, checked against every message already in memory).
    /// <see langword="null"/> for a caller that never sent one (every pre-`5-07` client) - dedup is
    /// simply unavailable for that send, never a validation failure forced on an old caller.</summary>
    public Guid? ClientMessageId { get; }

    /// <summary>`5-03`: "message references the attachment, not the reverse" (`file-storage.md`) -
    /// this is that reference. Set once, at construction, alongside the same
    /// <c>Conversation.AddVisitorMessage</c>/<c>AddOperatorMessage</c> call that assigns
    /// <see cref="Sequence"/> - never mutated afterward, since a message's attachment is decided when
    /// the message is sent, not edited in later.</summary>
    public AttachmentId? AttachmentId { get; }

    public DateTimeOffset CreatedAt { get; }

    // `14-06`: three backing fields rather than one owned entity, mapped by name in
    // MessageConfiguration (the shape Site.WidgetConfig already uses) - an EF owned type would bring
    // nullable-owned-entity ceremony for a value that is absent on almost every row. Kept private so
    // the only way to read them is Content below, which cannot hand back a half-populated value.
    private MessageContentKind? _contentKind;
    private MessagePayload? _payload;
    private List<MessageAction>? _actions;

    /// <summary>`14-06`: the structured half of this message - a kind, an opaque payload and a list
    /// of actions - or <see langword="null"/> for the prose message that is still the overwhelming
    /// majority. AGO Chat validates the payload's *shape* and never its meaning: it holds no schema
    /// for it, which is what keeps another product's vocabulary out of this assembly (`adr/0061`).
    ///
    /// <para><see cref="Body"/> stays mandatory even when this is present, and that single rule is
    /// the whole rendering contract - a channel with no UI renders the body and numbers the actions,
    /// and never has to parse a payload it may not understand.</para>
    ///
    /// <para>Computed rather than stored, and <c>Ignore</c>d by EF, so the three columns are the one
    /// source of truth and cannot disagree with a fourth copy.</para></summary>
    public MessageContent? Content =>
        _contentKind is null ? null : MessageContent.Materialize(_contentKind.Value, _payload, _actions ?? []);

    internal Message(
        MessageId id,
        ConversationId conversationId,
        int sequence,
        MessageAuthorKind authorKind,
        Guid authorId,
        MessageBody body,
        AttachmentId? attachmentId,
        Guid? clientMessageId,
        MessageContent? content,
        DateTimeOffset now,
        SiteId siteId,
        RetentionClass retentionClass)
    {
        Id = id;
        ConversationId = conversationId;
        Sequence = sequence;
        AuthorKind = authorKind;
        AuthorId = authorId;
        Body = body;
        AttachmentId = attachmentId;
        ClientMessageId = clientMessageId;
        CreatedAt = now;
        SiteId = siteId;
        RetentionClass = retentionClass;

        if (content is not null)
        {
            _contentKind = content.Kind;
            _payload = content.Payload;
            _actions = [.. content.Actions];
        }
    }

    // EF Core materialization only (1-04) - never called by domain code, not even by Conversation.
    private Message()
    {
    }
}
