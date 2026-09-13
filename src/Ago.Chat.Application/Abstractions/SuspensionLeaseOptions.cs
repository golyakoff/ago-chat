using System.ComponentModel.DataAnnotations;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `22-08`/`adr/0166`: the internal robustness lease's own two numbers - "not a second thing anyone
/// sets" through the product (`adr/0166`'s own words: this is never exposed to the platform owner, who
/// only ever chooses <c>suspended_until</c> in minutes), but bound from ordinary deployment
/// configuration with a validated default the same way every other interval in this codebase is
/// (<see cref="AnalyticsOptions"/>, <c>DemoTenantExpiryJobOptions</c>) - "not owner-configurable" and
/// "not a compiled-in literal with no test seam" are different claims, and only the first is
/// `adr/0166`'s own rule.
///
/// <para><b>Shared by three call sites across two hosts</b> - <c>SuspendTenantAsOwnerHandler</c> and
/// <c>ExtendSuspensionAsOwnerHandler</c> (both compute the lease's own <c>until</c> instant as "now
/// plus <see cref="LeaseLength"/>" the moment they publish <c>TenantSuspensionChanged</c>) and
/// <c>Ago.Chat.Worker.SuspensionLeaseRenewalJob</c> (which republishes the identical computation on
/// <see cref="RenewalInterval"/>'s own cadence for as long as a site stays suspended). One options
/// class, bound once in <c>ChatModule.ConfigureServices</c> (shared by every host, the same reason
/// <c>ModuleFlowReportOptions</c> is bound there rather than per-host) rather than three separate
/// bindings that could silently drift apart.</para>
///
/// <para><b>Defaults are `adr/0166`'s own stated numbers - 5 minutes, renewed at 2.5</b> - not
/// invented here: that ADR's own Decision section states both, reasoned from the asymmetry of a
/// continued violation against a broker outage, and explicitly not measured
/// (`CLAUDE.md`'s "measure or stay silent" is satisfied by naming the reasoning rather than a
/// number).</para>
/// </summary>
public sealed class SuspensionLeaseOptions
{
    public const string SectionName = "SuspensionLease";

    /// <summary>`adr/0166`'s own <b>L</b> - how long a module's own copy of "is this account suspended"
    /// may lag before it must fail closed.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan LeaseLength { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>`adr/0149` rule 1's own "renewed at half the lease length" - independently
    /// configurable rather than derived, so a test can shorten both without silently changing their
    /// ratio by accident.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:30:00")]
    public TimeSpan RenewalInterval { get; set; } = TimeSpan.FromMinutes(2.5);
}
