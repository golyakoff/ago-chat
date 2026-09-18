using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.MintVisitorChannelLinkCode;

/// <summary>
/// `25-148`: mints a fresh <see cref="Domain.PendingChannelLinkRequest"/> for a visitor session's own
/// handshake, for exactly one <see cref="ChannelKind"/> - Telegram, and only Telegram, in this item's own
/// scope (see <see cref="MintVisitorChannelLinkCodeHandler"/>'s own remarks for why the command itself
/// stays channel-neutral even though only one caller exists today).
/// </summary>
public sealed record MintVisitorChannelLinkCode(SiteId SiteId, VisitorId VisitorId, ChannelKind Kind);

/// <summary>The plaintext code, shown to nobody but embedded directly into the channel's own deep link -
/// see <see cref="MintVisitorChannelLinkCodeHandler"/>'s own remarks for why this never needs to be
/// "relayed" the way the console/`/linkidentity` originators' codes do.</summary>
public sealed record MintedVisitorChannelLinkCode(string Code, DateTimeOffset ExpiresAt);
