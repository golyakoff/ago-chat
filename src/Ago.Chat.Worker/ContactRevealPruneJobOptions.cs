namespace Ago.Chat.Worker;

/// <summary>Bound from <c>ContactRevealPruneJob:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule).</summary>
public sealed class ContactRevealPruneJobOptions
{
    public const string SectionName = "ContactRevealPruneJob";

    /// <summary>`23-11`'s own Scope: "its own retention" - a deliberate, unmeasured choice, the same
    /// family as <see cref="AccessRecordPruneJobOptions.RetentionWindow"/>'s own 365 days and for the
    /// identical reasoning: a reveal record is evidence of an ordinary, lawful read of one field this
    /// tenant's own setting chose to mask, not proof of a lawful basis or a completed erasure - the
    /// class of fact <c>acceptance_records</c>/<c>erasure_records</c> are allowed to keep indefinitely
    /// for a stated reason that does not transfer here. 365 days is long enough that "did anyone
    /// reveal this in the past year" still has an answer, short enough that this table does not
    /// become the indefinite personal-data store about an operator that `AccessRecordPruneJobOptions`'s
    /// own remarks warn a table like this must not be.</summary>
    public TimeSpan RetentionWindow { get; set; } = TimeSpan.FromDays(365);

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    public int BatchSize { get; set; } = 1000;

    public int MaxBatchesPerCycle { get; set; } = 50;
}
