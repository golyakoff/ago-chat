namespace Ago.Chat.Worker;

/// <summary>Bound from <c>PersonErasedConsumer:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule) - the identical shape
/// <see cref="PersonRegisteredConsumerOptions"/> already establishes for the opposite-direction
/// event.</summary>
public sealed class PersonErasedConsumerOptions
{
    public const string SectionName = "PersonErasedConsumer";

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
