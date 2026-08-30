namespace Ago.Chat.Worker;

/// <summary>Bound from `ConversationCategorizationJob:*` config keys, validated at startup
/// (naming-and-structure.md's options-validation rule). Every default below is a stated starting
/// point, not a measurement - CLAUDE.md's "measure, don't invent" rule, the same caveat every other
/// job options class in this project carries.</summary>
public sealed class ConversationCategorizationJobOptions
{
    public const string SectionName = "ConversationCategorizationJob";

    /// <summary>`adr/0078`'s kind 2: "run asynchronously after a conversation closes (or periodically
    /// over recent ones)" - fifteen minutes, frequent enough that a closed conversation is never more
    /// than that long from being classified, without hammering the provider once per tick the way a
    /// much shorter interval would.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Candidates classified per tick - the same batching shape
    /// <see cref="AutoCloseInactiveConversationsJobOptions.BatchSize"/> already uses, so one tick with
    /// an unusually large backlog of newly-closed conversations cannot hold a real-money provider call
    /// loop open indefinitely.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Only conversations closed within this long are eligible
    /// (<see cref="ConversationCategorizationQuery"/>'s own remarks on why this is also what bounds a
    /// zero-tag-vocabulary site's own re-scan cost). Twenty-four hours: long enough that a provider
    /// outage spanning a few ticks still leaves room to catch up before a conversation ages out,
    /// short enough that classifying a months-old conversation - whose topic is no longer "recent" in
    /// any operationally useful sense - never happens.</summary>
    public TimeSpan LookbackWindow { get; set; } = TimeSpan.FromHours(24);
}
