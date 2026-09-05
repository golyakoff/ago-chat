namespace Ago.Chat.Application.UseCases.PublishDocumentVersion;

/// <summary>
/// `24-02`: publish a new version of <paramref name="DocumentKey"/> with the given
/// <paramref name="Title"/>/<paramref name="Body"/>, right now. The version identifier is never a
/// caller-supplied field - <see cref="Domain.PublishedDocumentVersion"/>'s own remarks explain why it
/// is minted server-side instead.
///
/// <para>Deliberately carries no permission check of its own in its handler - the entire
/// access-control story is <c>OwnerDocumentEndpoints</c>'s <c>RequirePlatformOwner</c> gate, the same
/// single-gate shape every other owner surface in this codebase already uses
/// (<c>OwnerModuleEndpoints</c>'s own remarks). This command has no <c>SiteId</c> to check a permission
/// against in the first place - a document is not tenant-scoped, it is AGO's own.</para>
/// </summary>
public sealed record PublishDocumentVersion(string DocumentKey, string Title, string Body);
