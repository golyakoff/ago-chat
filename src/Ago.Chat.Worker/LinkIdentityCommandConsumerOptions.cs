namespace Ago.Chat.Worker;

/// <summary>Bound from <c>LinkIdentityCommandConsumer:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule) - the same shape
/// <see cref="ModuleTaskConsumerOptions"/>/<see cref="OfflineAutoReplyConsumerOptions"/> both already
/// establish for their own consumers of the same topic.</summary>
public sealed class LinkIdentityCommandConsumerOptions
{
    public const string SectionName = "LinkIdentityCommandConsumer";

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
