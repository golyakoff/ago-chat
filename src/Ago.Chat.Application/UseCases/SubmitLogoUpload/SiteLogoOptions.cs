namespace Ago.Chat.Application.UseCases.SubmitLogoUpload;

/// <summary>
/// `25-160`: bound from <c>SiteLogo:*</c> config keys - the same shape <c>AttachmentOptions</c> already
/// establishes for its own upload ceiling. <see cref="MaxSizeBytes"/> is the one, cheap,
/// decode-nothing check <see cref="SubmitLogoUploadHandler"/> ever runs (this backlog item's own Scope:
/// "a synchronous, cheap check only - raw byte-size ceiling before anything is decoded"); the real
/// authority - real pixel dimensions, real format, not animated - lives entirely in
/// `Ago.Chat.Worker.SiteLogoValidator`, which never trusts this handler's own pass.
///
/// 200 KiB is a judgement about what a small, static square logo needs, not a measurement - the same
/// "do not invent numbers... measure or stay silent" honesty every other `*Options` class in this
/// codebase already states for itself.
/// </summary>
public sealed class SiteLogoOptions
{
    public const string SectionName = "SiteLogo";

    public long MaxSizeBytes { get; set; } = 200 * 1024;

    /// <summary>The three formats this item's own Scope names - identical map shape to
    /// <c>AttachmentOptions.AllowedContentTypes</c>, and for the identical reason: the extension always
    /// comes from this server-controlled map, never from a client-supplied file name.</summary>
    public Dictionary<string, string> AllowedContentTypes { get; set; } = new()
    {
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/gif"] = ".gif",
    };

    /// <summary>The one dimension ceiling this item's own Scope names - checked for real only by
    /// `Ago.Chat.Worker.SiteLogoValidator`, after a real decode.</summary>
    public int MaxDimensionPixels { get; set; } = 100;

    /// <summary>Ephemeral, Worker/Api-internal presigned URL lifetime for the pending object - the
    /// identical <c>AttachmentThumbnailGenerator.UrlLifetime</c> shape and value, never client-facing.</summary>
    public TimeSpan InternalUploadLifetime { get; set; } = TimeSpan.FromMinutes(2);
}
