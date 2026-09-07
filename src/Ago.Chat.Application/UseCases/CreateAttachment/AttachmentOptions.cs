namespace Ago.Chat.Application.UseCases.CreateAttachment;

/// <summary>
/// Bound from <c>Attachments:*</c> config keys, validated at startup (naming-and-structure.md's
/// options-validation rule). <see cref="AllowedContentTypes"/> doubles as the object key's extension
/// source (<see cref="CreateAttachmentHandler"/>) - the extension always comes from this
/// server-controlled map, never from a client-supplied file name, so a client cannot smuggle an
/// executable extension onto an object whose declared content type says otherwise.
///
/// Defaults are a starting point, not measured or load-tested (`CLAUDE.md`: "do not invent
/// numbers... measure or stay silent") - Stage 7 gives this a real number.
/// </summary>
public sealed class AttachmentOptions
{
    public const string SectionName = "Attachments";

    public long MaxSizeBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>`23-75`: the conversation's own byte budget - not a per-file ceiling (that is
    /// <see cref="MaxSizeBytes"/> above), a running total across every attachment a conversation has
    /// ever reserved, spent by a visitor and an operator alike (`CreateAttachmentHandler`'s single
    /// shared <c>CreateAsync</c> path enforces it identically for both entry points). 100 MiB
    /// inherits this class's own honesty about its numbers: not measured or load-tested, a judgement
    /// about what a real support conversation's worth of screenshots and documents needs, stated here
    /// rather than left to acquire authority by being newer than <see cref="MaxSizeBytes"/>.</summary>
    public long MaxConversationBytes { get; set; } = 100 * 1024 * 1024;

    public Dictionary<string, string> AllowedContentTypes { get; set; } = new()
    {
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/gif"] = ".gif",
        ["image/webp"] = ".webp",
        ["application/pdf"] = ".pdf",
    };

    public TimeSpan UploadLifetime { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan DownloadLifetime { get; set; } = TimeSpan.FromMinutes(15);
}
