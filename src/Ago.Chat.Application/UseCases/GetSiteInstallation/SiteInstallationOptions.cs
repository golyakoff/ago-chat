namespace Ago.Chat.Application.UseCases.GetSiteInstallation;

/// <summary>
/// `23-06`: bound from <c>SiteInstallation:*</c> config keys, validated at startup - the same
/// "typo in a key must fail the pod, not silently disable a feature" shape every other
/// <c>*Options</c> class in this codebase already follows (`ModuleFlowReportOptions`'s own remarks).
///
/// <para><b>Why a config value and not a literal `7`.</b> `docs/design/decisions.md` §3 names the
/// number and says explicitly it is configuration: "Threshold for 'recently': 7 days, in
/// configuration." A one-chair salon and a high-volume shop may reasonably disagree about how many
/// quiet days still counts as "just installed" versus "long ago and still nothing" - the same
/// per-deployment judgement call `PenaltyPeriodMinutes` (decision 2) and cadence configuration
/// (decision 8) already make for their own thresholds.</para>
/// </summary>
public sealed class SiteInstallationOptions
{
    public const string SectionName = "SiteInstallation";

    /// <summary>How many days back <c>GetSiteInstallationHandler</c> looks for a conversation before
    /// concluding "the product was used" - `docs/backlog/23-06-*.md`'s own Scope: "any conversation
    /// created for this site in the window."</summary>
    public int RecentlyThresholdDays { get; set; } = 7;
}
