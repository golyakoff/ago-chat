namespace Ago.Chat.Domain;

/// <summary>
/// `23-19`: the fact `docs/design/decisions.md` §9 says is "already in hand and thrown away" - the
/// provider's own answer to one outbound channel send, recorded once per operator reply relayed out
/// through a linked channel. <c>DeliverChannelMessageHandler</c> is the only writer; this type never
/// transitions once written (`Record` is a plain, fully-parameterised factory, the same "no state
/// machine ahead of a real second caller" shape <see cref="WebhookDelivery"/>'s own remarks give for the
/// identical reason - nothing here is ever updated after the row lands).
///
/// <para><b>The address-or-reference decision, argued in full in the item's own report (`adr/0116`,
/// proposed).</b> This type carries <see cref="ChannelIdentityId"/>, never the raw
/// <see cref="ExternalChannelAddress"/>. A phone number copied into a second, append-only table once
/// per outbound message is exactly the "deletion journal" shape `personal-data.md` already rejected for
/// erasure - a growing store of the same personal data the platform already minimises to one row per
/// (site, channel, address) in <see cref="ChannelIdentity"/>. The reference costs nothing today because
/// <see cref="ChannelIdentity"/> is never hard-deleted except as part of a whole-site erasure
/// (<c>SiteErasureQuery.DeleteSiteAsync</c>'s cascade) - and that same cascade takes this table's own
/// rows down in the same statement (see <c>Infrastructure.Postgres.Persistence.ChannelDeliveryConfiguration</c>'s
/// own remarks), so the reference never dangles while anyone could still read it. An <c>Unlink</c>
/// (<see cref="ChannelIdentity.Unlink"/>) never deletes the row it marks inactive - "this number stopped
/// being this visitor" is a fact worth keeping, by that type's own remarks - so a tenant reading a
/// delivery record after the identity behind it was unlinked sees exactly the same channel and address
/// as before; unlinking changes routing going forward, never the historical record of what happened.</para>
///
/// <para><b><see cref="ChannelKind"/> is denormalised onto this row anyway</b>, even though
/// <see cref="ChannelIdentityId"/> could resolve it with a join - it is not personal data, it is already
/// known for free at write time (<c>Abstractions.OutboundChannelMessage.Kind</c>), and the per-conversation
/// read this item's own Scope asks for wants it without paying a join on every page.</para>
///
/// <para><b>Idempotent on <see cref="MessageId"/> alone.</b> One outbound send corresponds to exactly one
/// triggering operator message, and <c>MessageId</c> is that message's own already-globally-unique id -
/// the same "the provider's own idempotency key" role `resilience.md` already gives it. A redelivered
/// <c>MessageAccepted</c> collapses onto the same row via the unique index below, mirroring
/// <see cref="WebhookDelivery"/>'s own "insert-only, catch the unique violation" shape exactly - the
/// precedent this item's own backlog entry names by file.</para>
/// </summary>
public sealed class ChannelDelivery
{
    /// <summary>Bounded for the identical reason <see cref="WebhookDelivery.MaxResponseSnippetLength"/>
    /// is - a provider's failure reason is a short code or phrase, never an essay, and the invariant
    /// holds regardless of which adapter produced it.</summary>
    public const int MaxProviderDetailLength = 2000;

    public ChannelDeliveryId Id { get; }

    public SiteId SiteId { get; }

    /// <summary>No foreign key (<c>Infrastructure.Postgres.Persistence.ChannelDeliveryConfiguration</c>'s
    /// own remarks) - a conversation can be erased on its own, independently of the site
    /// (<c>ConversationErasureJob</c>), and this record must survive that the same way `adr/0112`'s
    /// erasure receipt and `adr/0113`'s access record survive the erasure of the row they are evidence
    /// about.</summary>
    public ConversationId ConversationId { get; }

    /// <summary>The triggering operator message's own id - this table's idempotency key. See this
    /// type's own remarks.</summary>
    public MessageId MessageId { get; }

    public ChannelKind ChannelKind { get; }

    /// <summary>The decided reference, not the address - see this type's own remarks in full.</summary>
    public ChannelIdentityId ChannelIdentityId { get; }

    public ChannelDeliveryStatus Status { get; }

    /// <summary>Set only when <see cref="Status"/> is <see cref="ChannelDeliveryStatus.Delivered"/> -
    /// whatever the provider called its own message, kept for support/diagnostics only; nothing routes
    /// on it (the same "never trusted, only shown" role <c>Abstractions.ChannelSendOutcome.ProviderMessageId</c>'s
    /// own remarks already give it).</summary>
    public string? ProviderMessageId { get; }

    /// <summary>Set only when <see cref="Status"/> is <see cref="ChannelDeliveryStatus.Refused"/> - the
    /// provider's own reason a tenant reads on the thread ("wrong number", "blocklisted", ...), never an
    /// engineer-only code (`flows.md` 4.5's own "must not be made to interpret a delivery status that
    /// means something only to an engineer" - the console is what turns this into that wording, this
    /// column only has to carry it).</summary>
    public string? FailureReason { get; }

    public DateTimeOffset AttemptedAt { get; }

    private ChannelDelivery(
        ChannelDeliveryId id,
        SiteId siteId,
        ConversationId conversationId,
        MessageId messageId,
        ChannelKind channelKind,
        ChannelIdentityId channelIdentityId,
        ChannelDeliveryStatus status,
        string? providerMessageId,
        string? failureReason,
        DateTimeOffset attemptedAt)
    {
        Id = id;
        SiteId = siteId;
        ConversationId = conversationId;
        MessageId = messageId;
        ChannelKind = channelKind;
        ChannelIdentityId = channelIdentityId;
        Status = status;
        ProviderMessageId = providerMessageId;
        FailureReason = Truncate(failureReason);
        AttemptedAt = attemptedAt;
    }

    // EF Core materialization only - never called by domain code.
    private ChannelDelivery()
    {
    }

    public static ChannelDelivery Record(
        ChannelDeliveryId id,
        SiteId siteId,
        ConversationId conversationId,
        MessageId messageId,
        ChannelKind channelKind,
        ChannelIdentityId channelIdentityId,
        ChannelDeliveryStatus status,
        string? providerMessageId,
        string? failureReason,
        DateTimeOffset attemptedAt) =>
        new(id, siteId, conversationId, messageId, channelKind, channelIdentityId, status, providerMessageId, failureReason, attemptedAt);

    private static string? Truncate(string? failureReason) =>
        failureReason is { Length: > MaxProviderDetailLength } ? failureReason[..MaxProviderDetailLength] : failureReason;
}
