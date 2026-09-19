namespace Ago.Chat.Worker;

/// <summary>Bound from <c>EntitlementWatchdogJob:*</c>. <see cref="Interval"/> is the one number this
/// item's own design actually specifies ("every one minute... a fixed one-minute cadence is the answer,
/// not an event-driven push for this particular fact" - the author's own call, not a measured figure);
/// every other job in this codebase that binds an <c>Interval</c> option still exposes it rather than
/// hardcoding it, the same "configurable even when the shipped value is fixed by design" precedent
/// <c>ConversationAssignmentJobOptions</c> already establishes.</summary>
public sealed class EntitlementWatchdogJobOptions
{
    public const string SectionName = "EntitlementWatchdogJob";

    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);
}
