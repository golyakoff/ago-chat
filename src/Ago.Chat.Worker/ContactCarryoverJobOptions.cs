namespace Ago.Chat.Worker;

/// <summary>Bound from <c>ContactCarryoverJob:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule).</summary>
public sealed class ContactCarryoverJobOptions
{
    public const string SectionName = "ContactCarryoverJob";

    /// <summary>How often a sweep cycle runs. A carry-over is already asynchronous by contract - the
    /// grant this item's own item names completes long before any contact is republished - so this is
    /// an operational default balancing "a newly granted tenant sees their history arrive promptly"
    /// against "do not add needless load to `visitor_contact_details`/`visitors`", not a measurement
    /// (`CLAUDE.md`: "do not invent numbers"). The same default <see cref="ConversationErasureJobOptions.Interval"/>
    /// already uses, for the identical reasoning.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How many sites with a pending carry-over one sweep cycle visits. Small, the same
    /// "external I/O-shaped per item, not a row" caution <see cref="SiteErasureJobOptions.BatchSize"/>
    /// gives, even though this job's own per-site work is plain SQL - a site needing a carry-over at
    /// all is expected to be rare (most grants concern a site with little or no prior contact history),
    /// so there is no benefit to visiting many at once and a real cost if one site's batch is unusually
    /// slow.</summary>
    public int SiteBatchSize { get; set; } = 10;

    /// <summary>How many contact details one site's own batch stages to the outbox per cycle - the
    /// same "a single unbounded statement on a hot-ish table is its own incident" reasoning
    /// <see cref="ConversationErasureJobOptions.MessageBatchSize"/> states, sized down from that job's
    /// 500 because each row here also becomes one outbox insert, not a plain delete.</summary>
    public int ContactBatchSize { get; set; } = 200;
}
