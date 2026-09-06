namespace Ago.Chat.Worker;

/// <summary>Bound from <c>SiteAllowedOriginsCacheInvalidationConsumer:*</c> config keys, validated at
/// startup (naming-and-structure.md's options-validation rule) - the same shape
/// <see cref="SiteCacheInvalidationConsumerOptions"/> already has, kept as its own section rather than
/// shared: the two consumers subscribe to different event types and retry independently, so a future
/// change to one's backoff should not silently move the other's.</summary>
public sealed class SiteAllowedOriginsCacheInvalidationConsumerOptions
{
    public const string SectionName = "SiteAllowedOriginsCacheInvalidationConsumer";

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
