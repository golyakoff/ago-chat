namespace Ago.Chat.Worker;

/// <summary>Bound from <c>SiteCacheInvalidationConsumer:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule).</summary>
public sealed class SiteCacheInvalidationConsumerOptions
{
    public const string SectionName = "SiteCacheInvalidationConsumer";

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
