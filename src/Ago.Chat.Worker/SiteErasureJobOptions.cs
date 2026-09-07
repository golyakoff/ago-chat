namespace Ago.Chat.Worker;

/// <summary>Bound from <c>SiteErasureJob:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule).</summary>
public sealed class SiteErasureJobOptions
{
    public const string SectionName = "SiteErasureJob";

    /// <summary>How often a sweep cycle runs. Deliberately the same default as
    /// <see cref="ConversationErasureJobOptions.Interval"/> - a site's own removal is gated on its
    /// conversations having drained (<see cref="SiteErasureQuery.HasAnyConversationAsync"/>), so
    /// running this job faster than the conversation job would only mean more no-op ticks spent
    /// finding conversations still remain; an operational default, not a measurement.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How many sites one sweep cycle claims. Small - a site's own tick reaches Keycloak once
    /// per operator plus a cache-invalidation publish, the same "external I/O per item, not a row"
    /// reasoning <see cref="ConversationErasureJobOptions.BatchSize"/> gives.</summary>
    public int BatchSize { get; set; } = 10;

    /// <summary>
    /// `22-30`: how long a module may leave a site's erasure request unconfirmed before the
    /// `erasure_records` receipt is marked <c>Failed</c>, naming the module - <see cref="SiteErasureJob.EraseModulesAsync"/>'s
    /// own remarks. An implementer's-call safety rail, not a measurement: this deployment has no
    /// recorded distribution for how long a module deployment might be down, and `CLAUDE.md` forbids
    /// inventing one - the same posture <c>EnableModuleForSiteAsOwnerHandler.MaxGrantDuration</c>
    /// already takes for its own unmeasured bound.
    ///
    /// <para>One hour, chosen from the shape of the two failure modes this bound actually separates,
    /// not from a target SLA (none is published for erasure completion). This job already retries
    /// every <see cref="Interval"/> regardless of this value - <see cref="ErasureRecordStatus.Failed"/>
    /// is not terminal, so nothing about retrying changes when this window elapses; what changes is
    /// only whether a person can *see* that a site is stuck. An hour is long enough that an ordinary
    /// module deployment restart or a brief network blip never trips it (avoiding a receipt that
    /// flaps between <c>Pending</c> and <c>Failed</c> on every transient hiccup), and short enough that
    /// a genuinely stuck erasure is visible on the same working day it started, not "day
    /// twenty-five" - the backlog's own example of the failure this bound exists to prevent.</para>
    /// </summary>
    public TimeSpan ModuleUnreachableWindow { get; set; } = TimeSpan.FromHours(1);
}
