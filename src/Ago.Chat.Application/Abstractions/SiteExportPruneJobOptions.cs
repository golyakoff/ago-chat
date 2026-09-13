namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// Bound from <c>SiteExportPruneJob:*</c> config keys, validated at startup (naming-and-structure.md's
/// options-validation rule) - the same shape every sibling prune-job options class in
/// <c>Ago.Chat.Worker</c> uses (<c>AccessRecordPruneJobOptions</c>, <c>WebhookDeliveryPruneJobOptions</c>).
///
/// <para><b>Lives in <c>Ago.Chat.Application</c>, not <c>Ago.Chat.Worker</c> - the one deliberate
/// departure from that sibling shape, and the reason is this class's second reader.</b> Every other
/// prune job's options class is consumed by exactly one type, its own job, so it lives next to it in
/// the Worker project. This one has two: <c>Ago.Chat.Worker</c>'s own <c>SiteExportPruneJob</c> (which
/// needs every property below to run its sweep), and <c>Ago.Chat.Application.UseCases.GetSiteExportHistory
/// .GetSiteExportHistoryHandler</c> (which needs only <see cref="RetentionWindow"/>, to compute when a
/// <c>Ready</c> row will be pruned). The dependency rule (`CLAUDE.md` rule 1) forbids the second reader
/// from taking a project reference on <c>Ago.Chat.Worker</c> - an Application-layer handler cannot
/// depend on a host-adjacent Infrastructure project just to read one <see cref="TimeSpan"/> - so the
/// type has to live somewhere both can already reach, which is Application itself. This mirrors
/// <c>SiteExportOptions</c>/<c>SiteExportRateLimitOptions</c> right next to it: both already live in
/// Application despite being consumed from a single use case each, because nothing about "where a
/// config POCO lives" is a Worker-only concern - it is wherever its readers actually are.</para>
///
/// <para><b>Why this must be one class rather than two independently-bound numbers.</b> This item's
/// own requirement is that the list screen's computed "auto-deletes on" date and the prune job's own
/// deletion sweep can never drift apart - the identical "one options class shared by both consumers,
/// not one per consumer" reasoning <c>MessageArchiveJob</c>'s own remarks give for reusing
/// <c>MessagePartitionPruneJobOptions</c> rather than binding a second, independently-configurable
/// horizon. The difference here is that the two consumers run in different host processes
/// (<c>Ago.Chat.Worker</c> and <c>Ago.Chat.Api</c>) rather than the same one - which a shared .NET
/// <see cref="TimeSpan"/> field cannot bridge by itself. What actually keeps the two hosts in step is
/// <see cref="SectionName"/> plus this single class: both hosts bind the identical config section, from
/// whatever configuration source they share (`appsettings.json`/environment/K8s ConfigMap - the same
/// source every other option in this codebase already reads its per-environment value from), through
/// <c>Ago.Chat.Module.ChatModule.ConfigureServices</c>, which runs in every host precisely so that
/// registrations like this one are written once rather than copy-pasted per `Program.cs`. There is
/// still exactly one place in the source where "seven days" and the config key name are written - never
/// two constants that could disagree - which is the property this item's Scope actually asked for.</para>
/// </summary>
public sealed class SiteExportPruneJobOptions
{
    public const string SectionName = "SiteExportPruneJob";

    /// <summary>
    /// How long a <c>Ready</c> export request's archive survives before <c>SiteExportPruneJob</c>
    /// deletes the object and marks the request <c>Expired</c>. Seven days, the same operational-default
    /// posture <c>AccessRecordPruneJobOptions.RetentionWindow</c>'s own remarks describe (CLAUDE.md:
    /// "do not invent numbers... a typical production figure" - this is a retention policy, not a
    /// benchmark): long enough that a tenant who requested an export and got distracted for a few days
    /// still finds it waiting, short enough that a forgotten archive does not sit in object storage
    /// indefinitely being nobody's problem. Distinct from, and not derived from,
    /// <c>SiteExportJobOptions.AttachmentUrlLifetime</c> - that number is a hard AWS SigV4 protocol
    /// ceiling for one presigned link embedded inside the archive (that class's own remarks: "the
    /// longest an AWS SigV4 presigned URL can express at all"), while this one is a retention policy for
    /// the archive object itself. Both happen to default to seven days today; that is a coincidence of
    /// two independent choices, not a shared source - changing one must never silently change the
    /// other, so they stay two separate properties in two separate classes.
    /// </summary>
    public TimeSpan RetentionWindow { get; set; } = TimeSpan.FromDays(7);

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Matches <c>AccessRecordPruneJobOptions.BatchSize</c>'s own reasoning in shape, but
    /// smaller - unlike a plain audit-row delete, resolving one row here costs a real object-storage
    /// DELETE call per item (<see cref="SiteExportPruneJobOptions"/>'s own class remarks), so a batch
    /// is sized for "a modest number of external calls per cycle," not "however many rows one DELETE
    /// statement can hold."</summary>
    public int BatchSize { get; set; } = 100;

    public int MaxBatchesPerCycle { get; set; } = 10;
}
