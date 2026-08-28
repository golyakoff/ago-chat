namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `13-01`: the plaintext invite code shown to the inviting operator exactly once, at generation
/// (`CreateOperatorInviteHandler`) - never stored or logged in that form (only its SHA-256 hash is
/// persisted, the same "handler hashes inline, no port needed for a deterministic pure function" shape
/// `RegisterChannelCredentialHandler` already uses for its own webhook secret hash).
///
/// <para><b>Its own port, not a reuse of <see cref="IWebhookSecretGenerator"/>.</b> `RegisterChannelCredentialHandler`
/// already reuses that generator for an unrelated bearer value (a MAX webhook secret), and that reuse is
/// harmless there because the value is only ever shown to MAX's own API, never to a person. An invite
/// code is copy-pasted by a human into Slack or email (this item's own Scope: "out-of-band, copied and
/// shared by the inviting admin however they choose") - showing them a value literally prefixed
/// `whsec_` would misdescribe what it is. Same entropy/generation reasoning as `adr/0024`'s
/// `IWebhookSecretGenerator` (256 bits from a CSPRNG, base64url-encoded), a different, honestly-named
/// prefix.</para>
/// </summary>
public interface IOperatorInviteCodeGenerator
{
    /// <summary>A high-entropy value, never a UUID or anything else with a fixed, guessable
    /// structure - the same reasoning <see cref="IWebhookSecretGenerator.NewSecret"/>'s own remarks
    /// give.</summary>
    string NewCode();
}
