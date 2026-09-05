namespace Ago.Chat.Application.UseCases.GetDocumentVersion;

/// <summary>
/// `24-02`: the published surface's own query - a specific <paramref name="Version"/> if the caller
/// names one, otherwise the document's current version. <see langword="null"/> is not "no opinion" the
/// way an optional filter usually is; it is the caller explicitly asking "what does this document say
/// right now", which is precisely what a visitor who has not yet accepted anything needs to be able to
/// ask with no account (`24-02`'s own Scope: "somebody who has not yet accepted anything has no account
/// to read it from").
/// </summary>
public sealed record GetDocumentVersion(string DocumentKey, string? Version);
