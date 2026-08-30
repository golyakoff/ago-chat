using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RequestChannelLinkFromConsole;

/// <summary>The plaintext code exists exactly once, in this response - never stored, never logged
/// (`Domain.PendingChannelLinkRequest`'s own remarks on why only its hash persists). The console's
/// composer quick-insert (`adr/0079` decision 2) is what turns this into the relay text an operator
/// sends the visitor; this handler does not compose that text itself, since the phrasing is a console
/// concern, not a fact this handler owns.</summary>
public sealed record RequestedChannelLink(string Code, DateTimeOffset ExpiresAt, ChannelKind Kind);
