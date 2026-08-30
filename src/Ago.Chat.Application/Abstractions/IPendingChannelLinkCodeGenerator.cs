namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `14-12`: the plaintext confirmation code shown to whoever requested a link - a console-displayed
/// value for an operator, or a system reply for a visitor's own <c>/linkidentity</c>.
///
/// <para><b>Its own port, not a reuse of <see cref="IWebhookSecretGenerator"/>/<see cref="IOperatorInviteCodeGenerator"/> -
/// deliberately low entropy, unlike either.</b> Both of those exist to be copy-pasted by a person into a
/// form or a config file; this one exists to be <em>typed</em> by a person into a chat window on a phone,
/// possibly with a numeric keypad, within a few minutes. A 256-bit base64url value would be nearly
/// impossible to retype correctly by hand - the entire mechanism (`adr/0079` decision 1: the visitor must
/// send it as a real message) requires a value short enough that this is not itself the obstacle. The
/// resulting brute-force surface is bounded by scope (site + channel kind + target visitor) and a short
/// <see cref="Domain.PendingChannelLinkRequest.ExpiresAt"/> window, not by code length - see that type's
/// own remarks on why <see cref="Domain.PendingChannelLinkRequest.CodeHash"/> still hashes it anyway.</para>
/// </summary>
public interface IPendingChannelLinkCodeGenerator
{
    /// <summary>A short, numeric, human-typeable code - never a UUID or anything with a fixed structure
    /// that reads as more secret than it is.</summary>
    string NewCode();
}
