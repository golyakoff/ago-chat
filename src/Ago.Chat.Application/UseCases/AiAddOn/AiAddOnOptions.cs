namespace Ago.Chat.Application.UseCases.AiAddOn;

/// <summary>
/// `25-04`: which <c>Ago.Chat.Domain.ModuleKey</c> this deployment sells the AI add-on under. Decision
/// 1 makes the add-on a third entry in `22-07`'s own module-quantity registry rather than a new
/// mechanism, and that registry's keys are deployment data - `adr/0159`'s own rule, and the reason
/// there is no <c>"ai"</c> literal anywhere in <c>Ago.Chat.*</c> source for
/// <c>Ago.Chat.Architecture.Tests.ModuleKeyLiteralRule</c> to catch.
///
/// <para><b>An options POCO, not a fourth <c>IConfiguration</c>-reading provider port</b>
/// (<c>IModuleEntryPointProvider</c>/<c>IModulePermissionsProvider</c>/<c>IBillingOptionEntitlementProvider</c>
/// are the three that exist). Those three resolve an <em>open-ended</em> key space - any module key a
/// caller names - which is precisely why they cannot be bound onto a class with one property per key.
/// This is a single deployment-wide value, the same shape <c>ModuleProvisioningOptions</c> already
/// binds, and a port for it would be ceremony around one string.</para>
///
/// <para><b>Blank is a refusal, never a default.</b> A deployment that has not said which key the
/// add-on is sold under cannot have sold it, so <see cref="AiProcessingGate"/> denies rather than
/// guessing - the same "a null is a legible refusal, never a hidden default" posture
/// <c>IModuleEntryPointProvider</c>'s own remarks state.</para>
/// </summary>
public sealed class AiAddOnOptions
{
    public const string SectionName = "AiAddOn";

    public string ModuleKey { get; set; } = string.Empty;
}
