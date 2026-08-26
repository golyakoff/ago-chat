namespace Ago.Chat.Worker;

/// <summary>Bound from <c>OfflineAutoReplyConsumer:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule) - the same shape
/// <see cref="UnreadCounterConsumerOptions"/> established for the other consumer of this topic.</summary>
public sealed class OfflineAutoReplyConsumerOptions
{
    public const string SectionName = "OfflineAutoReplyConsumer";

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
