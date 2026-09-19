namespace Ago.Chat.Worker;

/// <summary>Bound from <c>SiteLogoValidationConsumer:*</c> config keys, the identical shape
/// <c>AttachmentThumbnailConsumerOptions</c> already establishes.</summary>
public sealed class SiteLogoValidationConsumerOptions
{
    public const string SectionName = "SiteLogoValidationConsumer";

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
