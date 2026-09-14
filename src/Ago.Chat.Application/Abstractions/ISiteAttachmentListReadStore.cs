using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>`23-80`'s own read surface: "every attachment the tenant holds, in one table... sortable
/// by size, type, age, conversation and sender," plus the two filters that "carry judgement" (never
/// downloaded, duplicate content hash) and the "largest conversations" view. A Dapper read store
/// (adr/0004) - nothing here is a write, so it never touches <c>AgoChatDbContext</c>.
///
/// <para><b>Scoped to <see cref="AttachmentState.Ready"/> only.</b> A <c>Pending</c> row is not yet a
/// file the tenant actually holds (it may never be confirmed), and a <c>Deleted</c> row is exactly
/// what this screen exists to have already removed - showing it back on the same list it was deleted
/// from would contradict the delete the tenant just performed.</para></summary>
public interface ISiteAttachmentListReadStore
{
    Task<AttachmentListPage> ListAsync(
        SiteId siteId,
        AttachmentListSort sort,
        AttachmentListFilterKind filter,
        AttachmentListCursor? cursor,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>`23-80`'s own third view: "a conversation with forty small attachments can outweigh
    /// one big file, and nothing else on the screen would surface it." Grouped and ordered server-side
    /// - a tenant with many conversations should not have to fetch every attachment to compute this
    /// themselves.</summary>
    Task<IReadOnlyList<LargestConversationItem>> ListLargestConversationsAsync(
        SiteId siteId, int limit, CancellationToken cancellationToken);
}

/// <summary>"By size, descending — the default" (23-80's own words) is <see cref="SizeDescending"/>;
/// every other member is one of the three siblings the item names plus the two judgement filters'
/// natural companion sort. Not a <c>[Flags]</c> enum - a list has exactly one active sort at a
/// time.</summary>
public enum AttachmentListSort
{
    SizeDescending,
    TypeAscending,
    AgeAscending,
    ConversationAscending,
    SenderAscending,
}

/// <summary>The two filters `23-80`'s own text singles out as "carrying judgement," plus the default
/// of no filter at all.</summary>
public enum AttachmentListFilterKind
{
    None,
    NeverDownloaded,
    Duplicates,
}

/// <summary>A keyset cursor (data-model.md: "pagination is keyset... OFFSET is banned"), carrying the
/// last row's own sort-key value alongside its id as the tiebreaker - the same two-part shape every
/// sort order needs to stay stable when the sort key alone is not unique (two attachments of the
/// identical size, the identical content type, ...). <see cref="Value"/> is deliberately a plain
/// string rather than one typed field per possible sort column: the five sort orders in
/// <see cref="AttachmentListSort"/> have four different underlying column types (bytes, text, a
/// timestamp, a UUID), and a cursor a client round-trips verbatim between one page request and the
/// next has no reason to know which - <c>SiteAttachmentListReadStore</c> is the only place that
/// parses it back, per sort, the same place that produced it.</summary>
public sealed record AttachmentListCursor(string Value, Guid AttachmentId);

public sealed record AttachmentListItem(
    AttachmentId Id,
    ConversationId ConversationId,
    string ContentType,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    long DownloadCount,
    DateTimeOffset? LastDownloadedAt,
    MessageAuthorKind? SenderKind,
    Guid? SenderId,
    bool IsDuplicate);

public sealed record AttachmentListPage(IReadOnlyList<AttachmentListItem> Items, AttachmentListCursor? NextCursor);

public sealed record LargestConversationItem(ConversationId ConversationId, long TotalBytes, int AttachmentCount);
