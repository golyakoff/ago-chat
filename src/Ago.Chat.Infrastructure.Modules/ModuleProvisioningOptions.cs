namespace Ago.Chat.Infrastructure.Modules;

/// <summary>
/// `23-65`/`adr/0150`: `ModuleProvisioning:Secret` - the same config key name `secrets.md` already
/// used for the value <c>Ago.Calendar.Api</c>/<c>Ago.Faq.Api</c> hold in their own configuration
/// (`adr/0095`), now bound here too. The value must match whichever module deployment a grant or
/// revoke actually reaches - deploying this correctly is `ago-deploy`'s own job, out of this item's
/// scope, the same way `secrets.md` already separates "what a secret is" from "how it lands in a
/// manifest".
///
/// <para>Deliberately no fail-fast <c>.Validate()</c>/<c>.ValidateOnStart()</c> on this options class,
/// unlike <c>ChannelCredentialCipherOptions</c> right beside it in spirit - see
/// <see cref="Application.Abstractions.IModuleProvisioningSecretProvider"/>'s own remarks for why an
/// unset value here is a normal deployment state to be refused per call, not a boot-time
/// failure.</para>
/// </summary>
public sealed class ModuleProvisioningOptions
{
    public const string SectionName = "ModuleProvisioning";

    public string Secret { get; init; } = string.Empty;
}
