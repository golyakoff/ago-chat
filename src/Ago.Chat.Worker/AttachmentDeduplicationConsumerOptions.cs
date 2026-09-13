namespace Ago.Chat.Worker;

/// <summary>Bound from <c>AttachmentDeduplicationConsumer:*</c> config keys, matching
/// <see cref="AttachmentThumbnailConsumerOptions"/>'s own shape (naming-and-structure.md's
/// options-validation rule).</summary>
public sealed class AttachmentDeduplicationConsumerOptions
{
    public const string SectionName = "AttachmentDeduplicationConsumer";

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
