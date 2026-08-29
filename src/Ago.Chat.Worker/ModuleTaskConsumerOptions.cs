namespace Ago.Chat.Worker;

/// <summary>Bound from <c>ModuleTaskConsumer:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule) - the same shape
/// <see cref="OfflineAutoReplyConsumerOptions"/> establishes for its own consumer of the same topic.</summary>
public sealed class ModuleTaskConsumerOptions
{
    public const string SectionName = "ModuleTaskConsumer";

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
