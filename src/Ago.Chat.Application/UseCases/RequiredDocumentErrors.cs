using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases;

/// <summary>Error codes for `24-16`'s own two use cases - one small vocabulary, the same
/// one-file-per-feature-area shape <see cref="PublishedDocumentErrors"/> already establishes.</summary>
public static class RequiredDocumentErrors
{
    /// <summary>An empty or over-length document key - the caller's own mistake to fix, the same
    /// "reject before anything is written" shape <see cref="PublishedDocumentErrors.Invalid"/> already
    /// gives for the sibling document-key field on `24-02`'s own publish command.</summary>
    public static Error Invalid(string reason) => new("RequiredDocument.Invalid", reason);
}
