namespace Ago.Chat.Worker;

/// <summary>Bound from <c>PersonErasureJob:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule).</summary>
public sealed class PersonErasureJobOptions
{
    public const string SectionName = "PersonErasureJob";

    /// <summary>How often a sweep cycle runs. Deliberately the same default as
    /// <see cref="ConversationErasureJobOptions.Interval"/> - a person's own removal is gated on their
    /// conversations having drained (<see cref="PersonErasureQuery.HasAnyConversationAsync"/>), the
    /// identical "running this job faster only means more no-op ticks" reasoning
    /// <see cref="SiteErasureJobOptions.Interval"/>'s own remarks give for the whole-site version of the
    /// same wait.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How many persons one sweep cycle claims. Small, the same "an operational default, not a
    /// measurement" posture every other batch size on this page takes (CLAUDE.md: "do not invent
    /// numbers") - a person erasure is expected to be rare next to an ordinary message or outbox
    /// volume.</summary>
    public int BatchSize { get; set; } = 20;
}
