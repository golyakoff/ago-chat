using System.ComponentModel.DataAnnotations;

namespace Ago.Chat.Worker;

/// <summary>Bound from <c>DemoTenantExpiryJob:*</c>. Neither number is measured; both are shapes.</summary>
public sealed class DemoTenantExpiryJobOptions
{
    public const string SectionName = "DemoTenantExpiryJob";

    /// <summary>Five minutes against a lifetime measured in hours: a tenant outlives its window by a
    /// few minutes at worst, which nobody notices, and the sweep costs one indexed count when there is
    /// nothing to do. Sweeping every minute would buy precision nothing needs.</summary>
    [Range(typeof(TimeSpan), "00:00:10", "01:00:00")]
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How many tenants one pass may remove. Bounded for the same reason `15-04`'s prunes are:
    /// an unbounded delete is one bad `WHERE` clause away from being the incident. Twenty per five
    /// minutes drains any backlog this endpoint's own cap can produce.</summary>
    [Range(1, 1000)]
    public int BatchSize { get; set; } = 20;
}
