using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.DeliverChannelMessage;

/// <summary>
/// `14-02`: the outbound half of `14-01`'s port, proven for the first time - an operator's reply,
/// already committed (this is driven off `MessageAccepted`, never the send path itself; see the
/// handler's own remarks for why). Channel-neutral: nothing here names MAX, the same "which channel, if
/// any" question <see cref="Abstractions.IChannelIdentityRepository.FindMostRecentForVisitorAsync"/>
/// answers before this handler ever asks <see cref="Abstractions.IInboundChannelAdapterRegistry"/> for
/// an adapter.
/// </summary>
public sealed record DeliverChannelMessage(
    SiteId SiteId,
    ConversationId ConversationId,
    MessageId TriggerMessageId,
    MessageAuthorKind TriggerAuthorKind,
    int TriggerSequence);

public enum DeliverChannelMessageOutcome
{
    /// <summary>The common case for the message pipeline as a whole (visitor messages, and `14-03`'s
    /// own future system-authored auto-replies - see the handler's own remarks on why System is out of
    /// this item's scope) - not a failure, a correct decision not to act.</summary>
    NotAnOperatorMessage,

    /// <summary>An ordinary widget conversation - no <see cref="ChannelIdentity"/> is linked to this
    /// visitor, so there is nothing to relay out.</summary>
    NoLinkedChannel,

    /// <summary>The visitor's channel is linked, but this host runs no adapter for it - a supported
    /// configuration (<see cref="Abstractions.IInboundChannelAdapterRegistry"/>'s own remarks), not a
    /// bug; surfaced as a distinct outcome so it is visible in logs rather than silently swallowed.</summary>
    NoAdapterRegistered,

    /// <summary>The provider accepted the message.</summary>
    Delivered,

    /// <summary>The provider terminally refused the message (<see cref="Abstractions.ChannelSendOutcome.Refused"/>)
    /// - not retried, and not a system failure: `Domain.ChannelCredential`'s own remarks on revocation
    /// being "a rejected call at use time" is exactly this outcome for the specific case of a revoked or
    /// reset bot token.</summary>
    Refused,
}
