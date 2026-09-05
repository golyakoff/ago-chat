namespace Ago.Chat.Worker;

/// <summary>Bound from <c>AccessRecordPruneJob:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule).</summary>
public sealed class AccessRecordPruneJobOptions
{
    public const string SectionName = "AccessRecordPruneJob";

    /// <summary>`24-12`'s own Done-when: "the record has a stated retention enforced by something
    /// that runs" - unlike <c>acceptance_records</c>/<c>erasure_records</c>, which are indefinite
    /// evidence of a lawful basis or a completed erasure, an access record is itself personal data
    /// about the operator or platform owner who made the access (this item's own framing), and a
    /// second personal-data store kept forever is exactly the failure mode `24-12`'s own Scope warns
    /// against ("a personal-data store in its own right"). One year is a deliberate, unmeasured choice
    /// (`CLAUDE.md`: "do not invent numbers... a typical production figure" - this is not a benchmark,
    /// it is a retention policy, the same kind of choice `adr/0050`'s 30-day backup window and
    /// `WebhookDeliveryPruneJobOptions`'s 30-day window already are): long enough that "was this site's
    /// data read in the past year" - the question a tenant or an auditor actually asks - still has an
    /// answer, short enough that this table does not become the indefinite personal-data store
    /// `acceptance_records`/`erasure_records` are allowed to be for a different, stated reason (they
    /// are evidence of a legal event; this is a log of ordinary, lawful reads).</summary>
    public TimeSpan RetentionWindow { get; set; } = TimeSpan.FromDays(365);

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Matches <see cref="OutboxPruneJobOptions.BatchSize"/>'s own reasoning - no per-row
    /// external I/O, so the statement's own footprint is the only cost.</summary>
    public int BatchSize { get; set; } = 1000;

    public int MaxBatchesPerCycle { get; set; } = 50;
}
