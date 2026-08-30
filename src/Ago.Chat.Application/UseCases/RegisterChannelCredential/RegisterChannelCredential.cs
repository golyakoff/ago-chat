using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RegisterChannelCredential;

/// <summary>
/// <paramref name="ProviderAccountId"/> defaults to <see langword="null"/> - <see cref="Domain.ChannelCredential.ProviderAccountId"/>'s
/// own remarks explain why only VK's own connect endpoint ever populates it (MAX's and Telegram's bot
/// tokens are self-addressing and need nothing beyond the token itself). This command stays
/// channel-neutral by accepting it as an optional field rather than gaining a VK-only sibling command -
/// the identical "a credential concept that will need the identical shape for a later channel's own key
/// is not a MAX-only fact to begin with" reasoning `adr/0069` already applied to this whole type.
///
/// <para><paramref name="RefreshToken"/> is `14-11`'s own addition, the identical "optional, defaults to
/// null, only one channel's own connect endpoint ever populates it" shape - see
/// <see cref="Domain.ChannelCredential.RefreshTokenCiphertext"/>'s own remarks for why Avito's OAuth
/// credential needs it where every other channel's static token does not.</para>
/// </summary>
public sealed record RegisterChannelCredential(
    OperatorId RequestedBy, SiteId SiteId, ChannelKind Kind, string Token, string? ProviderAccountId = null,
    string? RefreshToken = null);

/// <summary>
/// <see cref="WebhookSecret"/> is the plaintext value AGO itself generated for this credential - not
/// the shop's own token (that is never returned; see <see cref="Domain.ChannelCredential"/>'s own
/// remarks). It exists in this result purely so the caller (the MAX-aware endpoint in
/// <c>Ago.Chat.Api</c>, which alone knows how to call the provider's own subscribe API) can hand it to
/// the provider once, at registration; <c>ChannelCredential</c> stores only its hash, so this is the one
/// and only place the value exists in a form anyone can use. `adr/0069`'s "the console never shows it
/// back" is about the shop's token, not this value.
///
/// <para><b>`14-08` update: MAX and Telegram never show this value to anyone, but VK's own connect
/// endpoint does</b> - VK's Callback API secret is entered by a human, into VK's own community settings
/// UI, not handed over through an API call the way MAX's <c>POST /subscriptions</c> does it
/// programmatically. That is not a new exception to `adr/0069`'s "console never shows it back": that
/// rule is about the shop's own token (<see cref="Domain.ChannelCredential.TokenCiphertext"/>), and
/// <see cref="Domain.ChannelCredential"/>'s own remarks already draw the contrast with
/// <c>WebhookEndpoint.SecretCiphertext</c>, "whose plaintext is legitimately shown to the tenant once at
/// registration (that secret is AGO's own value, generated for the tenant's benefit)" - this value is
/// exactly that kind of secret, and VK's own connect endpoint (<c>Ago.Chat.Api</c>'s
/// <c>VkChannelEndpoints</c>) is simply the first caller with a reason to actually show it.</para>
/// </summary>
public sealed record RegisteredChannelCredential(
    ChannelCredentialId ChannelCredentialId, ChannelKind Kind, string WebhookSecret, DateTimeOffset CreatedAt);
