using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RegisterChannelCredential;

public sealed record RegisterChannelCredential(OperatorId RequestedBy, SiteId SiteId, ChannelKind Kind, string Token);

/// <summary>
/// <see cref="WebhookSecret"/> is the plaintext value AGO itself generated for this credential - not
/// the shop's own token (that is never returned; see <see cref="Domain.ChannelCredential"/>'s own
/// remarks). It exists in this result purely so the caller (the MAX-aware endpoint in
/// <c>Ago.Chat.Api</c>, which alone knows how to call the provider's own subscribe API) can hand it to
/// the provider once, at registration; <c>ChannelCredential</c> stores only its hash, so this is the one
/// and only place the value exists in a form anyone can use. `adr/0069`'s "the console never shows it
/// back" is about the shop's token, not this value - this secret is never shown to the console or the
/// shop at all, by any caller, ever.
/// </summary>
public sealed record RegisteredChannelCredential(
    ChannelCredentialId ChannelCredentialId, ChannelKind Kind, string WebhookSecret, DateTimeOffset CreatedAt);
