namespace Ago.Chat.Application.UseCases.GetModuleFlowReportForSite;

/// <summary>
/// `18-14`: bound from <c>ModuleFlowReport:*</c> config keys, validated at startup
/// (`naming-and-structure.md`'s options-validation rule: "a typo in a key must fail the pod, not
/// silently disable a feature") - the same shape <c>MessageSendRateLimitOptions</c>/<c>YooKassaOptions</c>
/// already establish.
///
/// <para><b>Why a config value and not a literal.</b> This report exists to answer "how many
/// conversations started <em>the booking flow</em>", which today means the one module wired statically
/// (`adr/0065` §8): but the actual key that module registers under is data the site owner chose when
/// enabling it (<c>EnableModuleForSiteHandler</c>'s own <c>command.ModuleKey</c>), never a compile-time
/// constant anywhere in <c>Ago.Chat.*</c> - guard 9's third check (`Ago.Chat.Architecture.Tests.
/// ModuleKeyLiteralGuardTests`) fails the build on exactly that literal appearing in this assembly's
/// source. Reading it from configuration keeps the value out of `.cs` source entirely (the guard scans
/// `src/**/*.cs`, never `appsettings.json`) while still letting <c>GetModuleFlowReportForSiteHandler</c>
/// construct a real, validated <see cref="Domain.ModuleKey"/> from it at the one place that needs to
/// know which module this report is about.</para>
///
/// <para>No code default is given for <see cref="ModuleKey"/>, for the identical reason
/// <c>BillingOptions.PricePerSeatRub</c> ships none: an empty string would satisfy the CLR's default
/// binder with nothing to complain about, so <c>ChatModule</c>'s own <c>.Validate()</c> predicate (which
/// constructs a real <see cref="Domain.ModuleKey"/> from the bound value and rejects anything that
/// throws) is what turns a missing or malformed config key into a startup failure rather than a report
/// that silently returns zero for every site forever.</para>
/// </summary>
public sealed class ModuleFlowReportOptions
{
    public const string SectionName = "ModuleFlowReport";

    public string ModuleKey { get; set; } = string.Empty;
}
