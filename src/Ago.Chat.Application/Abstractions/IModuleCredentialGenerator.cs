namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `22-11`: produces the plaintext credential a rotation installs - the identical reason
/// <see cref="IWebhookSecretGenerator"/> exists rather than a handler calling
/// <c>RandomNumberGenerator</c> directly (that interface's own remarks: untestable for anything beyond
/// "a non-empty string came back", the same gap <see cref="IIdGenerator"/>/<see cref="IClock"/> close
/// for identity and time).
///
/// <para><b>Not used by <c>EnableModuleForSiteHandler</c>.</b> That handler still accepts an
/// operator-supplied <see cref="Domain.ModuleCredential"/>, unchanged from `22-02` - see
/// <c>RotateModuleCredentialHandler</c>'s own remarks for why rotation mints instead and enabling does
/// not.</para>
/// </summary>
public interface IModuleCredentialGenerator
{
    /// <summary>A high-entropy value shaped to satisfy <see cref="Domain.ModuleCredential"/>'s own
    /// bounds - never a UUID or anything else with a fixed, guessable structure.</summary>
    string NewCredential();
}
