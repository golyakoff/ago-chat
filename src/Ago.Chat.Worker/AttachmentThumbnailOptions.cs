namespace Ago.Chat.Worker;

/// <summary>Bound from <c>AttachmentThumbnail:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule). Dimensions and quality are an unmeasured
/// starting point (`CLAUDE.md`) - small enough to render fast in a chat history, large enough to be
/// recognisable.</summary>
public sealed class AttachmentThumbnailOptions
{
    public const string SectionName = "AttachmentThumbnail";

    public int MaxWidth { get; set; } = 256;

    public int MaxHeight { get; set; } = 256;

    public int JpegQuality { get; set; } = 80;
}
