namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `25-160`: turns a promoted logo's public object key into the plain, non-expiring URL the console's
/// own preview and (once ready) a visitor's reply email both resolve it from - `docs/backlog/25-160-*.md`'s
/// own Design decisions: "a public, long-lived, cache-friendly URL for the finished logo, not the
/// presigned-per-viewer model `IFileStorage`/attachments already use." <see cref="Ago.Platform.Abstractions.IFileStorage"/>
/// has no such method (by design, `adr/0008` - it is a presign-only port), so this is a small,
/// product-level port of its own rather than a platform change: `clean-architecture.md`'s own
/// "premature generalisation" warning applies to widening a platform abstraction for one caller this
/// item alone has today - a second product needing the identical "just glue the configured public
/// hostname to an object key" logic is what would justify moving it up into
/// `Ago.Platform.Abstractions` instead (the same `ago-platform`-as-NuGet trigger this item's own ADR
/// names for its sibling decision).
/// </summary>
public interface ISiteLogoPublicUrlBuilder
{
    string Build(string objectKey);
}
