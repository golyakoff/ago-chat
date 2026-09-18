using Ago.Chat.Application.UseCases.ReceiveChannelMessage;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RecordChannelVisitorContact;

/// <summary>
/// `25-151`: the inbound-channel sibling of <see cref="ReceiveChannelMessage"/> - deliberately its own
/// command rather than a new field on that one. <c>ChannelPortTests.
/// ReceiveChannelMessage_CarriesNoTimestamp</c>'s own remarks state the standing rule this follows:
/// <see cref="ReceiveChannelMessage"/> is guarded against exactly this kind of undocumented accretion,
/// and a contact share is not a message body at all - on both channels this item covers, a
/// contact-bearing update carries no <c>text</c> and would have nothing to put in
/// <see cref="ReceiveChannelMessage.Body"/> anyway. The precedent for "a second, narrower command beside
/// the message pipeline, not a widened one" is `14-12`'s own <c>HandleLinkIdentityCommand</c> - a
/// different mechanism in the details (that one reacts to an already-stored message via the outbox,
/// this one reacts to a channel-neutral fact <c>TelegramInboundMessageParser</c>/
/// <c>MaxInboundMessageParser</c> extract before a message ever exists to store), but the identical
/// shape at the level this item's own Scope text names it: a channel-provider fact that is not an
/// ordinary message body gets its own command, not a field bolted onto <see cref="ReceiveChannelMessage"/>.
///
/// <para><see cref="SiteId"/> is never caller-supplied, for the identical reason
/// <see cref="ReceiveChannelMessage.SiteId"/> is not - resolved by the concrete adapter from its own
/// configuration (the site whose bot token or credential received this update) before this command is
/// ever constructed. See <c>TenantScopeExemptions</c>'s own entry for this handler.</para>
///
/// <para><paramref name="Phone"/> arrives already verified by the calling channel's own trust rule -
/// Telegram's <c>contact.user_id == message.from.id</c> equality, or MAX's HMAC-SHA256 hash check
/// (<c>TelegramInboundMessageParser</c>/<c>MaxInboundMessageParser</c>'s own <c>TryVerifyContact</c>
/// remarks) - so this command and its handler have nothing left to verify about who the number belongs
/// to; recording it is the only work left.</para>
/// </summary>
public sealed record RecordChannelVisitorContact(
    SiteId SiteId,
    ChannelKind Kind,
    ExternalChannelAddress Sender,
    string Phone,
    string? Name);
