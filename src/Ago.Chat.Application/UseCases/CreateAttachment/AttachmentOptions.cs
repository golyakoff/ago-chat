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
