namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `22-11`: produces the plaintext credential a rotation installs - the identical reason
/// <see cref="IWebhookSecretGenerator"/> exists rather than a handler calling
/// <c>RandomNumberGenerator</c> directly (that interface's own remarks: untestable for anything beyond
/// "a non-empty string came back", the same gap <see cref="IIdGenerator"/>/<see cref="IClock"/> close
/// for identity and time).
///
/// <para><b>Not used by <c>EnableModuleForSiteAsOwnerHandler</c></b> - that handler still accepts a
/// caller-supplied <see cref="Domain.ModuleCredential"/>, unchanged from `22-02` (a platform owner
/// running a runbook can carry one). <c>RotateModuleCredentialAsOwnerHandler</c> mints instead, and so
/// does `26-316`'s tenant self-service <c>EnableModuleForSiteHandler</c>: a tenant clicking a toggle has
/// no credential to supply and no reason to, so the enable path that a tenant reaches generates one here
/// exactly as rotation does.</para>
/// </summary>
public interface IModuleCredentialGenerator
{
    /// <summary>A high-entropy value shaped to satisfy <see cref="Domain.ModuleCredential"/>'s own
    /// bounds - never a UUID or anything else with a fixed, guessable structure.</summary>
    string NewCredential();
}
