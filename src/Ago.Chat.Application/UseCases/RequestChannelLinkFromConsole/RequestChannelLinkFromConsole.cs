using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RequestChannelLinkFromConsole;

/// <summary>
/// `14-12`/`adr/0079`: an operator, mid-conversation, starts a verified link to a channel the visitor
/// just mentioned. See <see cref="RequestChannelLinkFromConsoleHandler"/>'s own remarks for why this is
/// gated on <see cref="Permission.ConversationSend"/> rather than a channel-management permission.
///
/// <para><paramref name="Kind"/> arrives as a raw string, not yet the validated <see cref="ChannelKind"/>
/// - <c>UpdateWidgetConfigHandler</c>'s own precedent for <c>Position</c>/<c>Locale</c>: the handler is
/// what validates it (its own remarks explain why that job belongs there, not the HTTP endpoint).</para>
/// </summary>
public sealed record RequestChannelLinkFromConsole(
    OperatorId RequestedBy, SiteId SiteId, ConversationId ConversationId, string Kind);
