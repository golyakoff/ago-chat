namespace Ago.Chat.Worker;

/// <summary>Bound from <c>ConnectionFanoutConsumer:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule).</summary>
public sealed class ConnectionFanoutConsumerOptions
{
    public const string SectionName = "ConnectionFanoutConsumer";

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
