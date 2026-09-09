namespace Ago.Chat.Worker;

/// <summary>Bound from <c>ModuleQuantityImpactComputedConsumer:*</c> config keys, validated at
/// startup (naming-and-structure.md's options-validation rule) - the identical defaults
/// <see cref="OperatorRemovedConsumerOptions"/> uses for its own sibling shape.</summary>
public sealed class ModuleQuantityImpactComputedConsumerOptions
{
    public const string SectionName = "ModuleQuantityImpactComputedConsumer";

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
