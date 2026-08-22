namespace Ago.Chat.Worker;

/// <summary>Bound from <c>AttachmentThumbnailConsumer:*</c> config keys, matching
/// <c>ConnectionFanoutConsumerOptions</c>'s own shape (naming-and-structure.md's options-validation
/// rule).</summary>
public sealed class AttachmentThumbnailConsumerOptions
{
    public const string SectionName = "AttachmentThumbnailConsumer";

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
