using System.ComponentModel.DataAnnotations;

namespace Ago.Chat.Worker;

/// <summary>Bound from <c>MessagePartitionPruneJob:*</c> config keys. Uses
/// <c>ValidateDataAnnotations</c> (matching <c>DemoTenantExpiryJobOptions</c>'s own precedent for a
/// group that needs a real range check, not just presence) rather than plain <c>ValidateOnStart</c> -
/// <see cref="RetentionHorizonMonths"/>'s floor is a correctness guarantee, not a preference.</summary>
public sealed class MessagePartitionPruneJobOptions
{
    public const string SectionName = "MessagePartitionPruneJob";

    /// <summary>Daily, matching <see cref="PartitionMaintenanceJobOptions.Interval"/> - the natural
    /// pairing: partitions change on a monthly boundary, so a check more often than daily buys
    /// nothing, and this job's own <c>DROP</c> decision is exactly as time-insensitive as that job's
    /// <c>CREATE</c> decision already is.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>`15-04`'s scope: "the horizon here is an operational default, chosen so the disk
    /// survives... configurable rather than baked into a migration." Three months is chosen for three
    /// reasons stated together because none alone would be enough: (1) it keeps `messages` - the
    /// largest table in the system and the one `2-06` partitioned specifically so old data could be
    /// dropped cheaply - to at most four or five live monthly partitions at a time (the current month,
    /// `PartitionMaintenanceJobOptions.MonthsAhead` = 2 future months always kept ready, plus up to
    /// three trailing months not yet past this horizon), bounding total index size the way `2-06`'s own
    /// rationale asks for; (2) it is deliberately shorter than any plausible real product retention
    /// window `13-05` might eventually decide - an operational default protecting a 2Gi disk should
    /// default toward safety, not presume a generous policy nobody has approved, and raising a config
    /// value later is cheap while a full disk is an outage now; (3) it is comfortably longer than every
    /// other operational cadence already in this system (`15-03`'s 24h alert repeat, `OutboxPruneJob`'s
    /// 24h window), so nobody's routine incident response is ever racing this horizon. Not a
    /// measurement - `15-05` supplies the real number (`adr/0031`), and this value is explicitly
    /// documented as what it replaces once that lands.</summary>
    [Range(1, int.MaxValue, ErrorMessage = "RetentionHorizonMonths must be at least 1 - a horizon of 0 or less would make the most recently completed month a drop candidate immediately, leaving no trailing month ever fully settled before eligibility.")]
    public int RetentionHorizonMonths { get; set; } = 3;
}
