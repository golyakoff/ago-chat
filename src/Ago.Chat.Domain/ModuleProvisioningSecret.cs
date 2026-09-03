namespace Ago.Chat.Domain;

/// <summary>
/// `22-11`: proves an `EnableModuleForSite`/`RotateModuleCredential`/`RevokeModuleForSite`/
/// `VerifyModuleRegistration` call is allowed to provision on behalf of the module deployment named by
/// <see cref="EnabledModule.EntryPoint"/> - supplied by the operator performing the call, the same way
/// <see cref="EnabledModule.EntryPoint"/> and <see cref="ModuleCredential"/> already are, and never
/// persisted anywhere on this side: it authenticates one outbound HTTP call and is then discarded, not
/// stored on <see cref="EnabledModule"/> or any other row.
///
/// <para><b>Opaque to Chat beyond shape, exactly like <see cref="ModuleCredential"/>.</b> Chat signs
/// nothing with it and never inspects its bytes beyond sending them verbatim in a header
/// (<c>Ago.Chat.Infrastructure.Modules.HttpModuleRegistrationGateway</c>'s own remarks); it does not
/// know or care whether the value matches Calendar's or FAQ's own configured secret.</para>
///
/// <para><b>Redacted <see cref="ToString"/>, for the identical reason <see cref="ModuleCredential"/>'s
/// own remarks give</b> - the standing rule that a minted or presented credential never lands in a log
/// line, exception message, or test failure output by accident.</para>
/// </summary>
public readonly record struct ModuleProvisioningSecret
{
    public const int MinLength = 16;

    public const int MaxLength = 256;

    public ModuleProvisioningSecret(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A module provisioning secret cannot be empty.", nameof(value));
        }

        var trimmed = value.Trim();
        if (trimmed.Length < MinLength)
        {
            throw new ArgumentException(
                $"A module provisioning secret must be at least {MinLength} characters long.", nameof(value));
        }

        if (trimmed.Length > MaxLength)
        {
            throw new ArgumentException(
                $"A module provisioning secret cannot exceed {MaxLength} characters.", nameof(value));
        }

        Value = trimmed;
    }

    public string Value { get; }

    public override string ToString() => "ModuleProvisioningSecret(***)";
}
