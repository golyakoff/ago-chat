using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-82`: the write half of the maintained per-tenant-per-month egress aggregate -
/// `site_attachment_egress` (see that table's own migration remarks,
/// `Stage23AddSiteAttachmentEgress`). Its own port, not folded into
/// <see cref="ISiteAttachmentStorageBudget"/>, even though both are counters on the same tenant: that
/// port is a compare-and-set a write decision depends on (CLAUDE.md rule 8 - "never cache what a write
/// decision depends on"), checked and reserved inside <c>CreateAttachmentHandler</c>'s own transaction
/// before a byte is ever accepted; this one is pure observability, recorded after the fact by
/// <c>GetAttachmentDownloadUrlHandler</c> and never consulted by anything that decides whether a
/// request may proceed. Giving the two the same interface would let a reader believe a download could
/// someday be refused by this count the way an upload is refused by that one - `23-82`'s own Scope is
/// explicit that deciding a ceiling is not this item's job ("decided with the tier grid... rather than
/// here").
///
/// <para><b>This is a proxy for real egress, not real egress itself</b> - stated here because every
/// caller needs to carry the same caveat forward rather than rediscover it: `GetAttachmentDownloadUrlHandler`
/// only calls <see cref="RecordAsync"/> when it mints a *fresh* presigned URL (a cache hit within that
/// URL's own TTL returns the same link without calling back here), and even a freshly presigned URL can
/// be fetched more than once, or not fetched at all, by whoever holds it - a browser may cache and
/// replay it, or a client may never actually GET it. The storage provider's own egress bill is the one
/// true figure; this one undercounts it and is never presented as anything else (23-82's own "Where this
/// is likely to go wrong" section).</para>
/// </summary>
public interface IAttachmentEgressMeter
{
    /// <summary>Increments <paramref name="siteId"/>'s row for <paramref name="periodMonth"/> by one
    /// download and <paramref name="bytes"/> bytes, creating the row on its first download of that
    /// month. <paramref name="periodMonth"/> is always the first day of its calendar month - see the
    /// owning migration's own remarks on why the column is a plain <c>date</c> bucket key rather than
    /// a <see cref="DateTimeOffset"/> instant.</summary>
    Task RecordAsync(SiteId siteId, DateOnly periodMonth, long bytes, CancellationToken cancellationToken);
}
